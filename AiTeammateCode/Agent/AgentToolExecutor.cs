using System.Diagnostics;
using System.Text.Json.Nodes;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Runs;
using AiTeammate.AiTeammateCode.Compat;
using AiTeammate.AiTeammateCode.Decision;

namespace AiTeammate.AiTeammateCode.Agent;

internal sealed class AgentToolResult
{
    public string Text { get; init; } = "";
    public string? ImageBase64 { get; init; }
    public string? ImageMimeType { get; init; }
    public bool IsError { get; init; }

    public static AgentToolResult Ok(string text) => new() { Text = text };

    public static AgentToolResult Error(string message) => new() { Text = message, IsError = true };
}

/// <summary>
/// Tool dispatcher shared by the TCP bridge. Read-only tools may run concurrently; actions are
/// serialized so two model calls can never drive the game at the same time.
/// </summary>
internal static class AgentToolExecutor
{
    private static readonly SemaphoreSlim ActionGate = new(1, 1);
    private const int MaxWaitSeconds = 50;

    public static async Task<AgentToolResult> ExecuteAsync(string name, JsonObject args,
        bool allowActions, CancellationToken ct)
    {
        try
        {
            switch (name)
            {
                case "sts2_hello":
                    return Hello();
                case "sts2_takeover":
                    // Invitation poll: a plugin may claim the run and create a dsh session for it.
                    return AgentToolResult.Ok(AgentTakeoverInvite.TryTakeoverJson());
                case "sts2_state":
                    return CaptureState(args);
                case "sts2_wait":
                    return await WaitAsync(args, ct);
                case "sts2_note":
                    // Run notes are not game actions; they stay available in read-only mode too.
                    return AddNote(args);
                case "sts2_play_cards":
                    if (!allowActions)
                        return AgentToolResult.Error("Action tools are disabled in the mod settings.");
                    return await PlayCardsAsync(args, ct);
                case "sts2_claim_rewards":
                    if (!allowActions)
                        return AgentToolResult.Error("Action tools are disabled in the mod settings.");
                    return await ClaimRewardsAsync(args, ct);
                case "sts2_read":
                    return Read(args);
                case "sts2_set_nickname":
                    // Cosmetic self-rename: allowed even in read-only mode, like sts2_read.
                    return SetNickname(args);
                case "sts2_chat_poll":
                    // The in-game chat panel is relayed through this long poll; the plugin only asks
                    // when it has a conversation that can answer. 聊天中继：插件取走玩家发言。
                    return await AgentChatRelay.PollAsync(args, ct);
                case "sts2_chat_send":
                    return ChatSend(args);
                case "sts2_screenshot":
                    return Screenshot();
            }

            if (!allowActions)
                return AgentToolResult.Error("Action tools are disabled in the mod settings.");

            return await RunActionAsync(name, args, ct);
        }
        catch (OperationCanceledException)
        {
            return AgentToolResult.Error("Cancelled (client disconnected or the game is shutting down).");
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Agent] tool {name} failed: {ex}");
            return AgentToolResult.Error($"Tool failed: {ex.Message}");
        }
    }

    private static AgentToolResult Hello()
    {
        var json = new JsonObject
        {
            ["name"] = "sts2-agent-bridge",
            ["bridge_version"] = "2.4.0",
            ["allow_actions"] = AgentBridgeService.AllowActions,
            ["agent_driving"] = AgentDecisionBroker.Enabled,
            ["chat"] = true,
            ["port"] = AgentBridgeService.Port,
            ["instructions"] =
                "Call sts2_state to inspect the game, sts2_wait to block until input is needed. "
                + "When sts2_state shows controlled.kind=local you play with the sts2_* UI actions "
                + "(play_card / use_potion / end_turn / choose / select_cards / proceed); a card choice "
                + "for that local player arrives as phase=ai_decision kind=hand_select and is answered "
                + "with sts2_decide. "
                + "when it shows kind=ai the AI player is yours and decisions come as phase=ai_decision, "
                + "answered with sts2_decide. "
                + "From the clean main menu start a singleplayer run with sts2_start_run {mode: "
                + "standard|daily|custom, character, ascension, seed, modifiers}, host a multiplayer lobby "
                + "with sts2_host {mode}, or resume with sts2_continue_run. The menu itself is visible: "
                + "phase=menu/char_select/daily_lobby/custom_lobby lists pressable options, sts2_choose "
                + "presses them and sts2_proceed goes back. "
                + "Run control is sts2_pause / sts2_resume / sts2_save / sts2_quit_to_menu / sts2_abandon; "
                + "sts2_quit_app quits the game process itself (automated tests use it). "
                + "sts2_set_nickname changes the AI teammate's display name at any time. "
                + "Record a long-run plan with sts2_note {text} — the mod shows it back in later "
                + "decision prompts and snapshots. "
                + "chat.pending in a snapshot lists player chat lines waiting for an answer; reply with "
                + "sts2_chat_send {text, reply_to: <pending seq>} (a reply_to that is not a live pending "
                + "seq is refused with the seqs that are), and use its directives field to record "
                + "tactics the player asked for."
        };
        return AgentToolResult.Ok(json.ToJsonString(AgentJson.Options));
    }

    private static AgentToolResult CaptureState(JsonObject args)
    {
        var snapshot = MainThread.Invoke(() => AgentSnapshotBuilder.Capture());
        if (IsDelta(args))
        {
            var delta = BuildDelta(snapshot);
            RememberSnapshot(snapshot);
            if (delta != null)
            {
                AddEnvelope(delta, snapshot, timedOut: false);
                return AgentToolResult.Ok(delta.ToJsonString(AgentJson.Options));
            }
        }

        RememberSnapshot(snapshot);
        return AgentToolResult.Ok(snapshot.Text);
    }

    private static AgentToolResult AddNote(JsonObject args)
    {
        var (ok, message) = AgentNotes.Add(StringArg(args, "text") ?? StringArg(args, "note"));
        return ok ? AgentToolResult.Ok(message) : AgentToolResult.Error(message);
    }

    private static bool IsDelta(JsonObject args)
        => string.Equals(StringArg(args, "detail"), "delta", StringComparison.OrdinalIgnoreCase);

    // ---- Snapshot cache for detail=delta -----------------------------------

    private static readonly Lock SnapshotSync = new();
    private static JsonObject? _lastSnapshotJson;

    private static void RememberSnapshot(AgentSnapshot snapshot)
    {
        try
        {
            lock (SnapshotSync)
                _lastSnapshotJson = (JsonObject)snapshot.Json.DeepClone();
        }
        catch { /* delta is an optimization; never break the call */ }
    }

    /// <summary>Top-level fields that changed since the last snapshot this process returned.</summary>
    private static JsonObject? BuildDelta(AgentSnapshot snapshot)
    {
        JsonObject? previous;
        lock (SnapshotSync)
            previous = _lastSnapshotJson;
        if (previous == null)
            return null; // first call: fall back to a full snapshot

        var changed = new JsonArray();
        var delta = new JsonObject();
        foreach (var pair in snapshot.Json)
        {
            if (pair.Key == "token")
                continue;
            if (!previous.TryGetPropertyValue(pair.Key, out var oldValue)
                || !JsonNode.DeepEquals(oldValue, pair.Value))
            {
                changed.Add(pair.Key);
                delta[pair.Key] = pair.Value?.DeepClone();
            }
        }

        foreach (var pair in previous)
        {
            if (!snapshot.Json.ContainsKey(pair.Key))
            {
                changed.Add(pair.Key);
                delta[pair.Key] = null;
            }
        }

        return new JsonObject
        {
            ["changed"] = changed,
            ["delta"] = delta
        };
    }

    private static void AddEnvelope(JsonObject delta, AgentSnapshot snapshot, bool timedOut)
    {
        delta["token"] = snapshot.Token;
        delta["phase"] = snapshot.Phase;
        delta["actionable"] = snapshot.Actionable;
        delta["blocked_by"] = snapshot.BlockedBy;
        if (timedOut)
            delta["wait_timed_out"] = true;
    }

    private static async Task<AgentToolResult> WaitAsync(JsonObject args, CancellationToken ct)
    {
        var afterToken = StringArg(args, "after_token");
        var timeout = Math.Clamp(IntArg(args, "timeout_seconds") ?? 25, 1, MaxWaitSeconds);
        var snapshot = await WaitForActionableAsync(afterToken, timeout, ct);
        if (snapshot == null)
            return AgentToolResult.Ok("{\"wait_timed_out\":true}");

        var timedOut = !snapshot.Actionable
                       || (!string.IsNullOrEmpty(afterToken) && snapshot.Token == afterToken);

        if (IsDelta(args))
        {
            var delta = BuildDelta(snapshot);
            RememberSnapshot(snapshot);
            if (delta != null)
            {
                AddEnvelope(delta, snapshot, timedOut);
                return AgentToolResult.Ok(delta.ToJsonString(AgentJson.Options));
            }
        }

        var json = (JsonObject)snapshot.Json.DeepClone();
        if (timedOut)
            json["wait_timed_out"] = true;
        RememberSnapshot(snapshot);
        return AgentToolResult.Ok(json.ToJsonString(AgentJson.Options));
    }

    /// <summary>
    /// Polls until the game is actionable and its state token moved away from <paramref name="afterToken"/>,
    /// then returns that snapshot (or the last one seen at the deadline). Poll fast: the mod's own
    /// observation delays already cover the animation, and extra latency here is pure dead time.
    /// 等待游戏进入可操作状态。
    /// </summary>
    private static async Task<AgentSnapshot?> WaitForActionableAsync(string? afterToken, int timeoutSeconds,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        AgentSnapshot? last = null;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                last = MainThread.Invoke(() => AgentSnapshotBuilder.Capture());
            }
            catch (TimeoutException)
            {
                // The main thread is busy loading; retry until the deadline.
            }

            if (last != null && last.Actionable
                && (string.IsNullOrEmpty(afterToken) || last.Token != afterToken))
            {
                return last;
            }

            if (stopwatch.Elapsed.TotalSeconds >= timeoutSeconds)
                return last;

            await Task.Delay(120, ct);
        }
    }

    private static AgentToolResult Read(JsonObject args)
    {
        var subject = (StringArg(args, "subject") ?? "").Trim().ToLowerInvariant();
        if (subject.Length == 0)
            return AgentToolResult.Error("Missing 'subject'. Use pile/relics/potions/player/run/map/deck_stats/card.");

        return MainThread.Invoke(() =>
        {
            var manager = AiTeammateManager.Instance;
            if (manager == null || !manager.IsInitialized)
                return AgentToolResult.Error("The AI teammate manager is not initialized yet.");

            var player = TryGetLocalPlayer(out var error);
            if (player == null)
                return AgentToolResult.Error(error);

            var call = new ToolCall();
            var runner = new AiToolRunner(new ContextBuilder(manager.QueryService), manager.QueryService);

            switch (subject)
            {
                case "pile":
                    call.Name = "read_pile";
                    CopyArg(args, call, "pile");
                    CopyArg(args, call, "detail");
                    CopyArg(args, call, "player");
                    break;
                case "relics":
                    call.Name = "read_relics";
                    CopyArg(args, call, "player");
                    break;
                case "potions":
                    call.Name = "read_potions";
                    CopyArg(args, call, "player");
                    break;
                case "player":
                    call.Name = "read_player";
                    CopyArg(args, call, "player");
                    break;
                case "run":
                    call.Name = "read_run";
                    break;
                case "map":
                    call.Name = "read_map";
                    break;
                case "deck_stats":
                    call.Name = "deck_stats";
                    CopyArg(args, call, "player");
                    break;
                case "card":
                    call.Name = "card_info";
                    CopyArg(args, call, "card", "name");
                    CopyArg(args, call, "player");
                    break;
                default:
                    return AgentToolResult.Error(
                        $"Unknown subject '{subject}'. Use pile/relics/potions/player/run/map/deck_stats/card.");
            }

            var result = runner.Execute(player, new[] { call });
            return AgentToolResult.Ok(result.Text);
        });
    }

    /// <summary>
    /// dsh writes a line into the in-game chat panel (the reply half of the chat relay).
    /// 外部 Agent 在游戏聊天面板里发言。
    /// </summary>
    private static AgentToolResult ChatSend(JsonObject args)
    {
        var (ok, message) = AgentChatRelay.SendReply(args);
        return ok ? AgentToolResult.Ok(message) : AgentToolResult.Error(message);
    }

    /// <summary>
    /// dsh changes the AI teammate's display name. Empty clears it and restores the personality name.
    /// 外部 Agent 修改 AI 昵称。
    /// </summary>
    private static AgentToolResult SetNickname(JsonObject args)
    {
        var name = StringArg(args, "name");
        if (string.IsNullOrWhiteSpace(name))
            name = StringArg(args, "nickname");

        var change = AiNames.SetNickname(name, "dsh");
        return change.Ok ? AgentToolResult.Ok(change.Message) : AgentToolResult.Error(change.Message);
    }

    private static AgentToolResult Screenshot()
    {
        return MainThread.Invoke(() =>
        {
            if (!AiTeammate.AiTeammateCode.Settings.AiSettingsStore.Current.EnableScreenshotTool)
            {
                return AgentToolResult.Error(
                    "Screenshots are disabled. Enable 'Let the AI see the game screen' in the mod settings.");
            }

            var shot = ScreenshotTool.Capture();
            if (!shot.IsSuccess)
                return AgentToolResult.Error(shot.Error);

            const string prefix = "data:image/jpeg;base64,";
            var base64 = shot.DataUrl.StartsWith(prefix, StringComparison.Ordinal)
                ? shot.DataUrl[prefix.Length..]
                : shot.DataUrl;

            return new AgentToolResult
            {
                Text = $"Captured the game screen ({shot.Width}×{shot.Height}).",
                ImageBase64 = base64,
                ImageMimeType = "image/jpeg"
            };
        });
    }

    private static async Task<AgentToolResult> RunActionAsync(string name, JsonObject args, CancellationToken ct)
    {
        await ActionGate.WaitAsync(ct);
        try
        {
            var beforeToken = CaptureToken();
            var action = MainThread.Invoke(() => AgentActions.Start(name, args));
            LogAction(name, action);

            await Task.Delay(Math.Clamp(action.ObserveMs, 0, 3000), ct);
            return await FinishAsync(action, beforeToken, args, ct);
        }
        finally
        {
            ActionGate.Release();
        }
    }

    /// <summary>
    /// Plays a whole list of cards (and optionally ends the turn) in ONE tool call, so a combat turn
    /// costs one model round trip instead of one per card. Stops at the first failing step and at any
    /// screen the caller has to answer (a card selection), reporting what it managed to do.
    /// 一次调用打完整回合。
    /// </summary>
    private static async Task<AgentToolResult> PlayCardsAsync(JsonObject args, CancellationToken ct)
    {
        var list = args["cards"] as JsonArray;
        if (list == null || list.Count == 0)
            return AgentToolResult.Error(
                "Missing 'cards'. Pass an array of {\"card\":\"<name|index>\",\"target\":\"<enemy>\"} (or plain card names).");

        await ActionGate.WaitAsync(ct);
        try
        {
            var steps = new JsonArray();
            var beforeToken = CaptureToken();
            var played = 0;
            var failed = false;
            var stopReason = "";

            foreach (var entry in list)
            {
                ct.ThrowIfCancellationRequested();
                if (entry == null)
                    continue;

                var stepArgs = new JsonObject();
                if (entry is JsonObject spec)
                {
                    foreach (var pair in spec)
                        stepArgs[pair.Key] = pair.Value?.DeepClone();
                }
                else
                {
                    try { stepArgs["card"] = entry.GetValue<string>(); }
                    catch { continue; }
                }

                AgentActionResult action;
                try
                {
                    action = MainThread.Invoke(() => AgentActions.Start("sts2_play_card", stepArgs));
                }
                catch (Exception ex)
                {
                    action = new AgentActionResult(false, "Play step failed: " + ex.Message, 0);
                }

                LogAction("sts2_play_card", action);
                steps.Add(new JsonObject { ["detail"] = action.Detail, ["ok"] = action.Ok });
                if (!action.Ok)
                {
                    failed = true;
                    stopReason = action.Detail;
                    break;
                }

                played++;
                await Task.Delay(Math.Clamp(action.ObserveMs, 0, 700), ct);

                // Cards that ask for another card (Headbutt, Armaments, ...) park the game on a
                // selection screen; stop so the caller picks instead of playing blind.
                var after = await CaptureOrNullAsync();
                if (after != null && after.Phase != "combat")
                {
                    stopReason = $"stopped on a {after.Phase} screen";
                    break;
                }
                if (after is { Actionable: false })
                {
                    stopReason = $"stopped: {after.BlockedBy}";
                    break;
                }
            }

            if (BoolArg(args, "end_turn") == true && !failed && string.IsNullOrEmpty(stopReason))
            {
                var endAction = MainThread.Invoke(() => AgentActions.Start("sts2_end_turn", new JsonObject()));
                LogAction("sts2_end_turn", endAction);
                steps.Add(new JsonObject { ["detail"] = endAction.Detail, ["ok"] = endAction.Ok });
                await Task.Delay(400, ct);
            }

            var detail = played == 0
                ? "Played no cards" + (stopReason.Length > 0 ? $": {stopReason}" : "")
                : $"Played {played} card(s)" + (stopReason.Length > 0 ? $"; {stopReason}" : "");

            return await FinishAsync(new AgentActionResult(!failed, detail, 0), beforeToken, args, ct, steps);
        }
        finally
        {
            ActionGate.Release();
        }
    }

    /// <summary>
    /// Claims every reward that needs no decision (gold / potion / relic), leaving card rewards for
    /// the caller to pick. Collapses the old claim-gold, claim-potion, claim-card dance into one call.
    /// 一键领取不需要选择的奖励。
    /// </summary>
    private static async Task<AgentToolResult> ClaimRewardsAsync(JsonObject args, CancellationToken ct)
    {
        await ActionGate.WaitAsync(ct);
        try
        {
            var steps = new JsonArray();
            var beforeToken = CaptureToken();
            var claimed = 0;

            for (var guard = 0; guard < 12; guard++)
            {
                ct.ThrowIfCancellationRequested();

                var snap = await CaptureOrNullAsync();
                if (snap == null || snap.Phase != "rewards")
                    break;

                var index = -1;
                var label = "";
                if (snap.Json["options"] is JsonArray options)
                {
                    for (var i = 0; i < options.Count; i++)
                    {
                        if (options[i] is not JsonObject option)
                            continue;
                        if (BoolArg(option, "enabled") == false)
                            continue;

                        var kind = option["kind"] is JsonNode kindNode ? kindNode.ToString() : "";
                        if (kind.Contains("CardReward", StringComparison.OrdinalIgnoreCase))
                            continue; // needs a decision

                        index = i;
                        label = option["label"]?.ToString() ?? kind;
                        break;
                    }
                }

                if (index < 0)
                    break;

                var action = MainThread.Invoke(() =>
                    AgentActions.Start("sts2_choose", new JsonObject { ["index"] = index }));
                LogAction("sts2_choose", action);
                steps.Add(new JsonObject { ["detail"] = action.Detail, ["ok"] = action.Ok, ["reward"] = label });
                if (!action.Ok)
                    break;

                claimed++;
                await Task.Delay(Math.Clamp(action.ObserveMs, 250, 800), ct);
            }

            var detail = claimed > 0
                ? $"Claimed {claimed} reward(s); card rewards are left for you to pick"
                : "Nothing claimable without a decision";
            return await FinishAsync(new AgentActionResult(true, detail, 0), beforeToken, args, ct, steps);
        }
        finally
        {
            ActionGate.Release();
        }
    }

    /// <summary>
    /// Builds the tool response. Unless the caller passed <c>wait:false</c>, this also blocks until the
    /// game is actionable again, which is what the plugin used to do with a separate sts2_wait call.
    /// 组装返回值，并把"等下一次可操作"折进动作里。
    /// </summary>
    private static async Task<AgentToolResult> FinishAsync(AgentActionResult action, string? beforeToken,
        JsonObject args, CancellationToken ct, JsonArray? steps = null)
    {
        var wait = BoolArg(args, "wait") ?? true;
        var waitSeconds = Math.Clamp(IntArg(args, "wait_seconds") ?? 20, 0, MaxWaitSeconds);

        AgentSnapshot? snapshot = null;
        var timedOut = false;

        if (action.Ok && wait && waitSeconds > 0)
        {
            snapshot = await WaitForActionableAsync(beforeToken, waitSeconds, ct);
            timedOut = snapshot == null || !snapshot.Actionable || snapshot.Token == beforeToken;
        }

        snapshot ??= await CaptureOrNullAsync();
        if (snapshot == null)
        {
            return action.Ok
                ? AgentToolResult.Ok($"{{\"ok\":true,\"detail\":\"{Escape(action.Detail)}\"}}")
                : AgentToolResult.Error(action.Detail);
        }

        // Keep the delta baseline in sync with the state the caller just saw.
        RememberSnapshot(snapshot);

        var state = (JsonObject)snapshot.Json.DeepClone();
        if (AgentActionState.ToJson() is { } lastAction)
            state["last_action"] = lastAction;

        var result = new JsonObject
        {
            ["ok"] = action.Ok,
            ["detail"] = action.Detail,
            ["token"] = snapshot.Token,
            ["phase"] = snapshot.Phase,
            ["actionable"] = snapshot.Actionable,
            ["state"] = state
        };
        if (timedOut)
            result["wait_timed_out"] = true;
        if (steps != null)
            result["steps"] = steps;

        return new AgentToolResult
        {
            Text = result.ToJsonString(AgentJson.Options),
            IsError = !action.Ok
        };
    }

    private static void LogAction(string name, AgentActionResult action)
    {
        try
        {
            AgentActionState.Record(name, action.Ok, action.Detail);
            AiTeammateManager.Instance?.AiLog.Log("AGENT",
                $"{(action.Ok ? "动作" : "拒绝")} {name}: {action.Detail}");
        }
        catch { /* logging must never break the call */ }
    }

    private static string? CaptureToken()
    {
        try { return MainThread.Invoke(() => AgentSnapshotBuilder.Capture()).Token; }
        catch { return null; }
    }

    private static async Task<AgentSnapshot?> CaptureOrNullAsync()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try { return MainThread.Invoke(() => AgentSnapshotBuilder.Capture()); }
            catch (TimeoutException) { await Task.Delay(120); }
            catch { return null; }
        }
        return null;
    }


    // ---- Helpers -------------------------------------------------------------------------

    private static Player? TryGetLocalPlayer(out string error)
    {
        error = "";
        var state = RunManager.Instance?.DebugOnlyGetState();
        if (state == null)
        {
            error = "No run is in progress.";
            return null;
        }

        if (LocalContext.NetId is not ulong netId || state.GetPlayer(netId) is not { } player)
        {
            error = "The local player is not in the run state yet.";
            return null;
        }

        return player;
    }

    private static void CopyArg(JsonObject args, ToolCall call, string source, string target)
    {
        var value = StringArg(args, source) ?? "";
        if (!string.IsNullOrWhiteSpace(value))
            call.Args[target] = value;
    }

    private static void CopyArg(JsonObject args, ToolCall call, string name) => CopyArg(args, call, name, name);

    private static string? StringArg(JsonObject args, string name)
    {
        try { return args[name]?.GetValue<string>(); }
        catch { return null; }
    }

    private static int? IntArg(JsonObject args, string name)
    {
        try
        {
            if (args[name] is not { } node) return null;
            return node.GetValue<int>();
        }
        catch
        {
            try { return int.TryParse(args[name]?.GetValue<string>(), out var value) ? value : null; }
            catch { return null; }
        }
    }

    private static bool? BoolArg(JsonObject args, string name)
    {
        try
        {
            if (args[name] is not { } node) return null;
            return node.GetValue<bool>();
        }
        catch
        {
            try { return bool.TryParse(args[name]?.GetValue<string>(), out var value) ? value : null; }
            catch { return null; }
        }
    }

    private static string Escape(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\r", "", StringComparison.Ordinal);
}
