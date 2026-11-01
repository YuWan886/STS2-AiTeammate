using System.Text.Json.Nodes;
using AiTeammate.AiTeammateCode.Chat;
using AiTeammate.AiTeammateCode.Compat;
using AiTeammate.AiTeammateCode.Settings;

namespace AiTeammate.AiTeammateCode.Agent;

/// <summary>How a relayed chat line ended: a rendered dsh reply, or "nobody answered in time".</summary>
internal readonly record struct AgentChatOutcome(ChatMessage? Message, bool Fallback);

/// <summary>
/// Routes the in-game chat panel to the attached dsh conversation.
///
/// The bridge is request/response only, so the game cannot push a chat line into dsh. Instead the
/// plugin long-polls <c>sts2_chat_poll</c> and injects what it receives into the conversation, and
/// the model answers with <c>sts2_chat_send</c>. Routing is gated on *observed* capability — a poll
/// seen within <see cref="PollFreshness"/> — so an older plugin, a connected-but-unbound dsh, or a
/// disabled bridge all keep the built-in backend exactly as before.
///
/// Every unanswered line is also exposed in the state snapshot (<c>chat.pending</c>) with its seq,
/// because a driving conversation often notices the player's line in the snapshot before the
/// plugin's injected follow-up reaches it; answering from that section must use the same seq the
/// long poll would have announced. A reply_to that does not match a live line is refused with the
/// waiting seqs instead of being rendered as a detached line, so the wait is not left hanging.
/// 聊天由 dsh 回复：插件轮询取走玩家发言，模型用 sts2_chat_send 发回来。
/// </summary>
internal static class AgentChatRelay
{
    private sealed class Entry
    {
        public int Seq;
        public string Sender = "";
        public string Text = "";
        public DateTime CreatedAtUtc = DateTime.UtcNow;
        public bool Delivered;
        public bool Answered;
        public bool AnsweredByFallback;
        public readonly TaskCompletionSource<ChatMessage?> Reply =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>A plugin poll this recent proves that someone can actually answer chat.</summary>
    private static readonly TimeSpan PollFreshness = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan EntryTtl = TimeSpan.FromMinutes(10);
    private const int MaxEntries = 50;
    private const int MaxTextLength = 1000;
    private const int PollCeilingSeconds = 25;
    private const int MaxPendingInSnapshot = 5;

    private static readonly Lock Sync = new();
    private static readonly List<Entry> Entries = new();
    private static int _sequence;
    private static DateTime _lastPollUtc = DateTime.MinValue;

    // ---- Gate ---------------------------------------------------------------------------

    /// <summary>True while dsh is expected to answer chat. 当前是否把聊天交给 dsh。</summary>
    public static bool ShouldRelay
    {
        get
        {
            try
            {
                if (!AiSettingsStore.Current.AgentChatToDsh)
                    return false;
            }
            catch
            {
                return false;
            }

            if (AgentBridgeService.ConnectedClients <= 0)
                return false;

            lock (Sync)
                return DateTime.UtcNow - _lastPollUtc <= PollFreshness;
        }
    }

    /// <summary>Marks a plugin poll; repeated polls keep the chat route alive. 插件轮询心跳。</summary>
    public static void NotePoll()
    {
        lock (Sync)
            _lastPollUtc = DateTime.UtcNow;
    }

    /// <summary>How many chat lines are still unanswered. 未回复的聊天条数。</summary>
    public static int UnreadCount
    {
        get
        {
            lock (Sync)
                return Entries.Count(entry => !entry.Answered);
        }
    }

    /// <summary>How long one chat line may wait for dsh before falling back. 等 dsh 回复聊天的秒数。</summary>
    public static int ReplyTimeoutSeconds => TimeoutSeconds;

    private static int TimeoutSeconds
    {
        get
        {
            try { return Math.Clamp(AiSettingsStore.Current.AgentChatReplyTimeoutSeconds, 15, 600); }
            catch { return 120; }
        }
    }

    private static bool FallbackEnabled
    {
        get
        {
            try { return AiSettingsStore.Current.AgentChatFallbackToBackend; }
            catch { return true; }
        }
    }

    // ---- Player line -> dsh -----------------------------------------------------------------

    /// <summary>Queues a player chat line for dsh when it is able to answer. 把玩家发言交给 dsh。</summary>
    public static bool TryRelay(string sender, string text, out int seq)
    {
        seq = 0;
        if (string.IsNullOrWhiteSpace(text) || !ShouldRelay)
            return false;

        lock (Sync)
        {
            PruneLocked();
            var entry = new Entry
            {
                Seq = ++_sequence,
                Sender = sender ?? "",
                Text = text.Trim()
            };
            Entries.Add(entry);
            seq = entry.Seq;
        }

        MainFile.Logger.Info($"[Agent] 聊天转交 dsh（seq={seq}）: {Truncate(text, 80)}");
        return true;
    }

    /// <summary>
    /// Waits for the dsh reply to <paramref name="seq"/>. Returns the rendered reply, or
    /// <c>Fallback=true</c> when the caller should let the built-in backend answer.
    /// </summary>
    public static async Task<AgentChatOutcome> AwaitReplyAsync(int seq, CancellationToken ct)
    {
        Entry? entry;
        lock (Sync)
            entry = Entries.FirstOrDefault(candidate => candidate.Seq == seq);

        if (entry == null)
            return new AgentChatOutcome(null, FallbackEnabled);

        var deadline = DateTime.UtcNow.AddSeconds(TimeoutSeconds);
        while (!entry.Reply.Task.IsCompleted)
        {
            if (ct.IsCancellationRequested)
                return new AgentChatOutcome(null, false);

            if (DateTime.UtcNow >= deadline)
                break;

            // dsh can go away mid-wait (plugin unloaded, settings switched off, client dropped).
            if (!ShouldRelay)
                break;

            try { await Task.Delay(250, ct); }
            catch (OperationCanceledException) { return new AgentChatOutcome(null, false); }
        }

        if (entry.Reply.Task.IsCompleted)
        {
            var completed = await entry.Reply.Task;
            // A completed task carrying null means the client went away; that is a fallback case,
            // not an answer.
            return completed != null
                ? new AgentChatOutcome(completed, false)
                : new AgentChatOutcome(null, FallbackEnabled);
        }

        if (!FallbackEnabled)
            return new AgentChatOutcome(null, false);

        if (MarkFallback(entry))
        {
            MainFile.Logger.Warn($"[Agent] dsh 未在 {TimeoutSeconds} 秒内回复聊天（seq={seq}），改由自带后端回复");
            return new AgentChatOutcome(null, true);
        }

        // A dsh reply won the race: it marks the entry answered before rendering and completing the
        // task, so wait a moment for that render instead of answering twice.
        try
        {
            var settled = await entry.Reply.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            if (settled != null)
                return new AgentChatOutcome(settled, false);
        }
        catch (TimeoutException) { }
        catch (OperationCanceledException) { }

        return new AgentChatOutcome(null, true);
    }

    private static bool MarkFallback(Entry entry)
    {
        lock (Sync)
        {
            if (entry.Answered)
                return false;

            entry.Answered = true;
            entry.AnsweredByFallback = true;
            return true;
        }
    }

    /// <summary>Client disconnected: let every waiter fall back instead of waiting for the timeout.</summary>
    public static void AbortPending(string reason)
    {
        lock (Sync)
        {
            foreach (var entry in Entries.Where(entry => !entry.Answered))
                entry.Reply.TrySetResult(null);
        }

        MainFile.Logger.Info($"[Agent] 取消等待中的聊天回复: {reason}");
    }

    // ---- dsh -> chat panel ------------------------------------------------------------------

    /// <summary>Long-polls the queued chat lines for the dsh plugin. 插件取走待回复的玩家发言。</summary>
    public static async Task<AgentToolResult> PollAsync(JsonObject args, CancellationToken ct)
    {
        var afterSeq = IntArg(args, "after_seq") ?? 0;
        var waitSeconds = Math.Clamp(IntArg(args, "wait_seconds") ?? 15, 0, PollCeilingSeconds);
        var deadline = DateTime.UtcNow.AddSeconds(waitSeconds);

        NotePoll();

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            NotePoll();

            var (seq, messages) = Collect(afterSeq);
            if (messages.Count > 0 || waitSeconds == 0 || DateTime.UtcNow >= deadline)
            {
                var json = new JsonObject
                {
                    ["seq"] = seq,
                    ["messages"] = messages
                };
                if (messages.Count == 0)
                    json["wait_timed_out"] = true;
                return AgentToolResult.Ok(json.ToJsonString(AgentJson.Options));
            }

            await Task.Delay(200, ct);
        }
    }

    private static (int Seq, JsonArray Messages) Collect(int afterSeq)
    {
        var messages = new JsonArray();
        int seq;

        lock (Sync)
        {
            PruneLocked();
            foreach (var entry in Entries.Where(candidate =>
                         candidate.Seq > afterSeq && !candidate.Delivered && !candidate.Answered))
            {
                entry.Delivered = true;
                messages.Add(new JsonObject
                {
                    ["seq"] = entry.Seq,
                    ["from"] = entry.Sender,
                    ["text"] = Truncate(entry.Text, MaxTextLength),
                    ["kind"] = "player",
                    ["at"] = entry.CreatedAtUtc.ToString("O")
                });
            }

            seq = Entries.Count > 0 ? Entries[^1].Seq : Math.Max(afterSeq, _sequence);
        }

        return (seq, messages);
    }

    /// <summary>Posts a line from dsh into the chat panel, optionally answering a queued message.</summary>
    public static (bool Ok, string Message) SendReply(JsonObject args)
    {
        var text = (StringArg(args, "text") ?? StringArg(args, "reply") ?? StringArg(args, "message") ?? "").Trim();
        if (text.Length == 0)
            return (false, "Missing 'text'. Pass the line you want the player to see in the chat panel.");

        var replyTo = IntArg(args, "reply_to") ?? IntArg(args, "replyTo") ?? 0;
        var nickname = StringArg(args, "nickname") ?? "";
        var directives = args["directives"] as JsonArray;

        Entry? entry = null;
        var note = "";
        lock (Sync)
        {
            PruneLocked();
            var open = Entries.Where(candidate => !candidate.Answered).ToList();
            if (replyTo > 0)
            {
                entry = Entries.FirstOrDefault(candidate => candidate.Seq == replyTo);
                if (entry == null)
                {
                    // A reply_to that matches no live line would silently miss the line the player
                    // is waiting for: the entry can be pruned after 10 minutes, the mod can have
                    // restarted, or the model simply guessed a seq. Refuse and hand back the live
                    // seqs so the very same text can be posted again correctly.
                    var waiting = open.Count == 0
                        ? "No chat line is waiting for an answer right now."
                        : "Chat lines still waiting: " + string.Join(", ", open.Select(candidate => candidate.Seq)) + ".";
                    return (false, $"reply_to {replyTo} is not an open chat line. {waiting} "
                                   + "Send the same text again with reply_to set to one of those seqs, "
                                   + "or without reply_to to post it as a new line.");
                }

                if (entry.Answered)
                {
                    if (!entry.AnsweredByFallback)
                        return (false, "That chat message was already answered. Do not repeat it; continue playing.");

                    // The built-in backend answered after the wait expired. The attached agent's
                    // answer is still the better one for the player, so post it instead of
                    // swallowing it — that used to look like "dsh cannot reply".
                    note = " (the built-in backend had already answered it after the wait, posted as the late reply)";
                }
                else
                {
                    entry.Answered = true;
                }
            }
            else
            {
                // Tolerate a missing reply_to: with exactly one open line, a free-form answer can
                // only be the answer to it. This keeps the fallback from double-answering.
                if (open.Count == 1)
                {
                    entry = open[0];
                    entry.Answered = true;
                }
                else if (open.Count > 0)
                {
                    // Several lines are open; posting this one leaves them unanswered, so tell dsh
                    // which seqs still need a reply instead of letting the wait time out.
                    note = " (posted as a new line; chat lines still waiting: "
                           + string.Join(", ", open.Select(candidate => candidate.Seq)) + ")";
                }
            }
        }

        var sender = entry?.Sender ?? "";
        ChatMessage? rendered = null;
        var postedAs = "";
        try
        {
            rendered = MainThread.Invoke(() =>
            {
                postedAs = AiNames.DisplayName;
                return AiTeammateManager.Instance?.ChatManager
                    ?.ApplyDshReply(text, directives, nickname, sender);
            });
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Agent] 聊天回复渲染失败: {ex.Message}");
            entry?.Reply.TrySetResult(null);
            return (false, "Could not post the chat message: " + ex.Message);
        }

        if (rendered == null)
        {
            entry?.Reply.TrySetResult(null);
            return (false, "The chat panel is not available right now.");
        }

        entry?.Reply.TrySetResult(rendered);
        MainFile.Logger.Info($"[Agent] dsh 回复聊天{(entry != null ? $"（seq={entry.Seq}）" : "")}: {Truncate(text, 80)}{(note.Length > 0 ? " [late/free-form]" : "")}");
        return (true, $"Posted to the in-game chat panel as {postedAs}.{note}");
    }

    // ---- Snapshot ---------------------------------------------------------------------------

    /// <summary>Chat section of every state snapshot. Main-thread only (reads the chat history).</summary>
    public static JsonObject SnapshotJson()
    {
        var recent = new JsonArray();
        try
        {
            var chat = AiTeammateManager.Instance?.ChatManager;
            if (chat != null)
            {
                foreach (var message in chat.SnapshotRecent(4).Where(message => message.Type == ChatMessageType.Normal))
                {
                    recent.Add(new JsonObject
                    {
                        ["from"] = message.SenderName,
                        ["text"] = Truncate(message.Content, 200),
                        ["ai"] = message.IsAi
                    });
                }
            }
        }
        catch
        {
            // The snapshot must never fail because the chat history is mid-mutation.
        }

        // Every unanswered player line, carrying the seq sts2_chat_send wants. A conversation that
        // notices the chat in the snapshot before the plugin's injected follow-up lands can answer
        // it directly instead of guessing a reply_to (the guess used to leave the line hanging until
        // the backend fallback, which is what produced two different answers to one message).
        var pending = new JsonArray();
        int unread;
        lock (Sync)
        {
            PruneLocked();
            foreach (var entry in Entries.Where(entry => !entry.Answered).OrderBy(entry => entry.Seq)
                         .Take(MaxPendingInSnapshot))
            {
                pending.Add(new JsonObject
                {
                    ["seq"] = entry.Seq,
                    ["from"] = entry.Sender,
                    ["text"] = Truncate(entry.Text, 300),
                    ["age_seconds"] = Math.Round((DateTime.UtcNow - entry.CreatedAtUtc).TotalSeconds, 1)
                });
            }

            unread = Entries.Count(entry => !entry.Answered);
        }

        return new JsonObject
        {
            ["capable"] = ShouldRelay,
            ["unread"] = unread,
            ["pending"] = pending,
            ["recent"] = recent
        };
    }

    // ---- Helpers ----------------------------------------------------------------------------

    private static void PruneLocked()
    {
        var cutoff = DateTime.UtcNow - EntryTtl;
        Entries.RemoveAll(entry => entry.CreatedAtUtc < cutoff);
        if (Entries.Count > MaxEntries)
            Entries.RemoveRange(0, Entries.Count - MaxEntries);
    }

    private static string Truncate(string text, int max)
        => string.IsNullOrEmpty(text) || text.Length <= max ? text ?? "" : text[..max] + "…";

    private static string? StringArg(JsonObject args, string name)
    {
        try { return args[name]?.GetValue<string>(); }
        catch { return null; }
    }

    private static int? IntArg(JsonObject args, string name)
    {
        try
        {
            if (args[name] is not { } node)
                return null;
            return node.GetValue<int>();
        }
        catch
        {
            try { return int.TryParse(args[name]?.GetValue<string>(), out var value) ? value : null; }
            catch { return null; }
        }
    }
}
