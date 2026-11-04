using System.Text.Json;
using System.Text.Json.Nodes;
using MegaCrit.Sts2.Core.Entities.Players;
using AiTeammate.AiTeammateCode.Settings;

namespace AiTeammate.AiTeammateCode.Agent;

/// <summary>
/// Lets an attached dsh client act as the brain of the AI player.
///
/// The mod keeps its hands (combat executor, room automation, reward claiming, map voting, sync),
/// but instead of asking the configured LLM backend for a decision it parks the call here and waits
/// for <c>sts2_decide</c>. The client sees the exact prompt the LLM would have seen and answers with
/// the same JSON the parser expects, so nothing downstream changes.
/// 对接 dsh 后，AI 的决策改由 dsh 给出（dsh 当大脑，mod 当手）。
///
/// A connected client is not the same as a driving one: the dsh host keeps its socket open while no
/// agent turn is running. If dsh has not touched the bridge for <see cref="FallbackSeconds"/>, the
/// broker stops parking decisions so the AI falls back to its own backend instead of freezing the game.
/// 连着 ≠ 在驱动：dsh 静默太久时改回自带后端，避免 AI 被卡住。
/// </summary>
internal static class AgentDecisionBroker
{
    private const int MaxWaitSeconds = 300;
    private const int WaitFloorSeconds = 60;
    private const int PollMs = 500;

    private sealed class Pending
    {
        public string Id = "";
        public string Kind = "";
        public string PlayerName = "";
        public ulong PlayerNetId;
        public string SystemPrompt = "";
        public string Prompt = "";
        public JsonArray? Options;
        public DateTimeOffset CreatedAt = DateTimeOffset.UtcNow;
        public readonly TaskCompletionSource<string?> Completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static readonly Lock Sync = new();
    private static Pending? _pending;
    private static int _sequence;
    private static string _lastResolvedId = "";
    private static string _lastResolvedKind = "";

    /// <summary>Id/kind of the decision accepted last, for the execution feedback snapshot.</summary>
    public static string LastResolvedId
    {
        get { lock (Sync) return _lastResolvedId; }
    }

    public static string LastResolvedKind
    {
        get { lock (Sync) return _lastResolvedKind; }
    }

    /// <summary>
    /// True when an external agent is attached with action rights: it becomes the AI's decision source.
    /// 外部 Agent 正在接管 AI 的大脑。
    /// </summary>
    public static bool Enabled => AgentBridgeService.ExternalAgentDriving;

    public static bool HasPending
    {
        get { lock (Sync) return _pending != null; }
    }

    /// <summary>
    /// How long dsh may stay silent before decisions go straight to the built-in backend.
    /// 0 disables the fallback. dsh 静默多少秒后回退到自带后端；0 = 不回退。
    /// </summary>
    private static int FallbackSeconds
    {
        get
        {
            try { return Math.Clamp(AiSettingsStore.Current.AgentFallbackSeconds, 0, 3600); }
            catch { return 30; }
        }
    }

    /// <summary>
    /// How long one parked decision may wait for its answer. Three times the silence window (at least
    /// a minute) so a model that thinks for a while is not cut off, while a client that died mid-turn
    /// still releases the AI. 单次决策的等待预算。
    /// </summary>
    private static int WaitBudgetSeconds(int fallbackSeconds)
        => fallbackSeconds <= 0
            ? MaxWaitSeconds
            : Math.Clamp(fallbackSeconds * 3, WaitFloorSeconds, MaxWaitSeconds);

    private static string DescribeSilence(double seconds)
        => seconds >= 3600 ? "超过 1 小时" : $"{seconds:F0} 秒";

    /// <summary>JSON view of the outstanding decision, or null. Main-thread safe.</summary>
    public static JsonObject? SnapshotJson()
    {
        lock (Sync)
        {
            if (_pending == null)
                return null;

            return new JsonObject
            {
                ["id"] = _pending.Id,
                ["kind"] = _pending.Kind,
                ["player"] = _pending.PlayerName,
                ["net_id"] = _pending.PlayerNetId,
                ["age_seconds"] = Math.Round((DateTimeOffset.UtcNow - _pending.CreatedAt).TotalSeconds, 1),
                ["max_wait_seconds"] = WaitBudgetSeconds(FallbackSeconds),
                ["system_prompt"] = _pending.SystemPrompt,
                ["prompt"] = _pending.Prompt,
                ["options"] = _pending.Options?.DeepClone(),
                ["answer_with"] = "sts2_decide {decision: <the JSON object the prompt asks for>}"
            };
        }
    }

    /// <summary>
    /// Blocks the AI's decision call until the client answers, the client goes away, or the wait
    /// budget expires. Returns null when nothing usable arrived (callers fall back to their defaults).
    /// </summary>
    public static async Task<string?> RequestAsync(Player aiPlayer, string kind, string systemPrompt,
        string prompt, CancellationToken ct, JsonArray? options = null)
    {
        if (!Enabled)
            return null;

        var fallbackSeconds = FallbackSeconds;
        if (fallbackSeconds > 0)
        {
            var idleSeconds = AgentBridgeService.ClientIdleSeconds;
            if (idleSeconds > fallbackSeconds)
            {
                // A socket stays open while no agent turn is running. Don't make the AI (and the other
                // players) wait for an answer that nobody is going to write.
                MainFile.Logger.Info(
                    $"[Agent] dsh 已静默 {DescribeSilence(idleSeconds)}，本次 {kind} 决策改用自带后端");
                return null;
            }
        }

        var waitBudgetSeconds = WaitBudgetSeconds(fallbackSeconds);
        Pending pending;
        Pending? superseded = null;
        lock (Sync)
        {
            // Only one decision can be outstanding: resolve a stale one instead of leaving its
            // caller waiting for the full timeout.
            superseded = _pending;
            pending = new Pending
            {
                Id = "d" + (++_sequence),
                Kind = kind,
                PlayerName = aiPlayer?.Character?.Id.Entry ?? "ai",
                PlayerNetId = aiPlayer?.NetId ?? 0,
                SystemPrompt = systemPrompt,
                Prompt = prompt,
                Options = options?.DeepClone() as JsonArray
            };
            _pending = pending;
        }

        if (superseded != null)
        {
            MainFile.Logger.Warn($"[Agent] decision {superseded.Id} superseded by {pending.Id}");
            superseded.Completion.TrySetResult(null);
        }

        MainFile.Logger.Info($"[Agent] waiting for dsh decision {pending.Id} ({kind})");
        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(waitBudgetSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            while (!pending.Completion.Task.IsCompleted)
            {
                if (!Enabled)
                    break; // client disconnected or actions disabled mid-wait

                try
                {
                    await Task.Delay(PollMs, linkedCts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            if (pending.Completion.Task.IsCompleted)
            {
                var content = await pending.Completion.Task;
                if (!string.IsNullOrWhiteSpace(content))
                    MainFile.Logger.Info($"[Agent] dsh answered decision {pending.Id} ({kind})");
                else
                    MainFile.Logger.Info($"[Agent] decision {pending.Id} ({kind}) cancelled");
                return content;
            }

            MainFile.Logger.Warn(
                $"[Agent] dsh 未在 {waitBudgetSeconds} 秒内应答 {pending.Id} ({kind})，回退自带后端");
            return null;
        }
        finally
        {
            lock (Sync)
            {
                if (ReferenceEquals(_pending, pending))
                    _pending = null;
            }

            pending.Completion.TrySetResult(null);
        }
    }

    /// <summary>Completes the parked decision with the client's answer. Returns an error string on refusal.</summary>
    public static (bool Ok, string Message) Resolve(string decision)
    {
        Pending? pending;
        lock (Sync)
            pending = _pending;

        if (pending == null)
            return (false, "No decision is pending right now. Call sts2_state / sts2_wait first.");

        var content = Normalize(decision);
        if (content.Length == 0)
            return (false, "Missing 'decision'. Pass the JSON object the prompt asked for.");

        if (!LooksLikeDecision(content, out var error))
            return (false, error + " The decision stays pending; answer again with valid JSON.");

        lock (Sync)
        {
            if (ReferenceEquals(_pending, pending))
                _pending = null;
            _lastResolvedId = pending.Id;
            _lastResolvedKind = pending.Kind;
        }

        pending.Completion.TrySetResult(content);
        return (true, $"Answered the {pending.Kind} decision ({pending.Id}).");
    }

    /// <summary>Unblocks a pending decision with a failure, e.g. when the client disconnects.</summary>
    public static void Abort(string reason)
    {
        Pending? pending;
        lock (Sync)
        {
            pending = _pending;
            _pending = null;
        }

        if (pending == null)
            return;

        MainFile.Logger.Info($"[Agent] decision {pending.Id} aborted: {reason}");
        pending.Completion.TrySetResult(null);
    }

    // ---- Helpers -------------------------------------------------------------------------

    private static string Normalize(string decision)
    {
        var text = (decision ?? "").Trim();
        if (text.Length == 0)
            return "";

        // Tolerate fenced code blocks: the agent may wrap the object as a markdown code block.
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = text.IndexOf('\n');
            if (firstNewline >= 0)
                text = text[(firstNewline + 1)..];
            if (text.EndsWith("```", StringComparison.Ordinal))
                text = text[..^3];
            text = text.Trim();
        }

        return text;
    }

    /// <summary>
    /// Very light sanity check: it must be a JSON object carrying at least one field the mod's
    /// parsers look for. Rejecting early is better than letting the game fall back silently.
    /// </summary>
    private static bool LooksLikeDecision(string content, out string error)
    {
        error = "";
        try
        {
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "The decision must be a JSON object.";
                return false;
            }

            string[] known =
            {
                "actions", "end_turn", "choice", "choices", "indices", "index", "upgrade_indices",
                "thinking", "reason", "chat_message", "deferred_actions", "reroll"
            };

            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (known.Any(k => string.Equals(k, property.Name, StringComparison.OrdinalIgnoreCase)))
                    return true;
            }

            error = "That JSON has none of the fields this decision expects "
                    + "(actions / end_turn / choice / choices / indices / upgrade_indices).";
            return false;
        }
        catch (JsonException ex)
        {
            error = "Invalid JSON: " + ex.Message;
            return false;
        }
    }
}
