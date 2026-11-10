using System.Text;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using AiTeammate.AiTeammateCode;
using AiTeammate.AiTeammateCode.Agent;
using AiTeammate.AiTeammateCode.Backend;
using AiTeammate.AiTeammateCode.Cache;
using AiTeammate.AiTeammateCode.Compat;
using AiTeammate.AiTeammateCode.Context;
using AiTeammate.AiTeammateCode.Personality;
using AiTeammate.AiTeammateCode.Settings;
using AiTeammate.AiTeammateCode.Solo;

namespace AiTeammate.AiTeammateCode.Decision;

public class DecisionService
{
    private readonly BackendManager _backend;
    private readonly PersonalityPromptBuilder _promptBuilder;
    private readonly ContextBuilder _contextBuilder;
    private readonly PersonalityManager _personalityManager;
    private readonly MemoryManager _memoryManager;
    private readonly ConversationContextManager _conversationContext;
    private readonly AiToolRunner _tools;

    public DecisionService(
        BackendManager backend,
        PersonalityManager personality,
        ContentQueryService query,
        ConversationContextManager conversationContext)
    {
        _backend = backend;
        _promptBuilder = new PersonalityPromptBuilder(personality);
        _contextBuilder = new ContextBuilder(query);
        _personalityManager = personality;
        _memoryManager = new MemoryManager();
        _conversationContext = conversationContext;
        _tools = new AiToolRunner(_contextBuilder, query);
    }

    private string BuildSystemPrompt(bool includeTeamPrinciples)
    {
        // Solo climb has no teammates; team-coordination rules would only add noise.
        var withTeam = includeTeamPrinciples && !SoloRunController.IsActive;

        // dsh brings its own persona: don't layer the mod's personality profile on top of it.
        // dsh 接管时人格由它自己决定，不再注入 mod 的人格设定。
        var prompt = AgentDecisionBroker.Enabled
            ? PersonalityPromptBuilder.BuildExternalAgentSystemPrompt(requireJson: true, includeTeamPrinciples: withTeam)
            : _promptBuilder.BuildSystemPrompt(requireJson: true, includeTeamPrinciples: withTeam);

        return withTeam
            ? _promptBuilder.AppendTeamPrinciples(prompt)
            : prompt;
    }

    /// <summary>
    /// 发送 AI 请求（支持 JSON 模式和多轮对话）。
    /// 所有决策类请求默认启用 json_object 模式。
    /// </summary>
    private async Task<AiResponse> SendDecisionAsync(
        string systemPrompt, string userPrompt,
        int maxTokens, float temp,
        List<AiMessage>? history = null,
        CancellationToken ct = default,
        int? timeoutOverrideMs = null,
        string? imageDataUrl = null,
        int? baseTimeoutMs = null)
    {
        var settings = AiSettingsStore.Current;
        var timeoutMs = Math.Clamp(timeoutOverrideMs ?? baseTimeoutMs ?? settings.DecisionTimeoutMs, 5000, 180000);
        using var timeoutCts = new CancellationTokenSource(timeoutMs);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        return await _backend.SendAsync(new AiRequest
        {
            SystemPrompt = systemPrompt,
            UserPrompt = userPrompt,
            Type = AiRequestType.Decision,
            MaxTokens = maxTokens,
            Temperature = temp,
            EnableJsonMode = true,       // DeepSeek JSON Output
            TopP = 0.9f,                 // DeepSeek 推荐与低温配合使用
            FrequencyPenalty = 0.1f,     // 轻微抑制重复
            ConversationHistory = history ?? new List<AiMessage>(),
            UserImageDataUrl = imageDataUrl
        }, linkedCts.Token);
    }

    /// <summary>
    /// 发送非战斗 AI 请求，带重试机制。
    /// 当 AI 后端返回错误（超时、网络异常等）时自动重试。
    /// </summary>
    private async Task<AiResponse> SendNonCombatDecisionWithRetryAsync(
        string systemPrompt, string userPrompt,
        int maxTokens, float temp,
        List<AiMessage>? history = null,
        CancellationToken ct = default,
        int? timeoutOverrideMs = null,
        string? imageDataUrl = null,
        int? baseTimeoutMs = null)
    {
        int maxAttempts = Math.Clamp(AiSettingsStore.Current.NonCombatRetryCount, 0, 5) + 1; // 首次尝试 + 重试次数
        AiResponse? lastResponse = null;

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (attempt > 0)
            {
                // 重试前等待，递增延迟
                await Task.Delay(500 * attempt, ct);
                MainFile.Logger.Info($"[DecisionService] 非战斗决策重试 ({attempt}/{maxAttempts - 1})");
            }

            lastResponse = await SendDecisionAsync(
                systemPrompt, userPrompt, maxTokens, temp, history, ct, timeoutOverrideMs, imageDataUrl,
                baseTimeoutMs);

            if (lastResponse.IsSuccess)
                return lastResponse;

            // 如果是因为外部取消（非超时），不重试
            if (ct.IsCancellationRequested)
                return lastResponse;
        }

        MainFile.Logger.Warn($"[DecisionService] 非战斗决策 {maxAttempts - 1} 次重试均失败");
        return lastResponse!;
    }

    /// <summary>
    /// Sends a decision request and runs any read-only tool calls the model asks for, then re-asks.
    /// The initial prompt enters the history exactly once; tool results plus the follow-up
    /// instruction form the final user message so roles stay user/assistant alternating.
    /// <paramref name="hasAnswer"/> marks responses that already contain a usable decision — those
    /// win over tool calls so the turn never stalls. Returns the last response unchanged when the
    /// tool budget is spent; callers fall back to their own defaults in that case.
    /// </summary>
    private async Task<AiResponse> RequestWithToolsAsync(
        Player aiPlayer,
        string systemPrompt,
        string firstPrompt,
        int maxTokens,
        float temp,
        int maxToolRounds,
        int toolRoundTimeoutMs,
        Func<System.Text.Json.JsonElement, bool> hasAnswer,
        bool retryOnFailure,
        CancellationToken ct,
        string kind = "decision")
    {
        // dsh takeover: an attached external agent answers this decision instead of the LLM backend.
        // The parked call resumes as soon as sts2_decide arrives, or falls back on timeout/disconnect.
        // Inline read-only tool calls work here too: the tools run on the main thread and the
        // follow-up decision is parked again with the results, exactly like the LLM loop below.
        if (AgentDecisionBroker.Enabled)
        {
            var externalPrompt = AppendRecentChat(firstPrompt);
            var answer = await AgentDecisionBroker.RequestAsync(aiPlayer, kind, systemPrompt, externalPrompt, ct);

            for (var round = 0; round < maxToolRounds; round++)
            {
                if (string.IsNullOrWhiteSpace(answer))
                    break;

                var calls = ResponseParser.ExtractToolCalls(answer);
                if (calls.Count == 0 || HasAnswer(answer, hasAnswer))
                    break;

                // The human interrupted mid-decision in solo climb: don't spend another request.
                if (SoloRunController.IsActive && AiFlowControl.IsPaused)
                    break;

                var toolResult = MainThread.Invoke(() => _tools.Execute(aiPlayer, calls));
                LogToolCalls(calls, toolResult.Text);
                externalPrompt = AppendRecentChat(firstPrompt) +
                                 "\n\n工具结果：\n" + toolResult.Text +
                                 "\n\n请基于上面的工具结果，直接输出最终决策 JSON；不要再次调用工具。";
                answer = await AgentDecisionBroker.RequestAsync(aiPlayer, kind, systemPrompt, externalPrompt, ct);
            }

            if (!string.IsNullOrWhiteSpace(answer))
                return new AiResponse { Content = answer, IsSuccess = true };
        }

        var history = new List<AiMessage>();
        var prompt = firstPrompt;
        string? followUpImage = null;

        // Combat keeps its own (settings) per-request timeout; every out-of-combat kind uses the
        // configurable out-of-combat limit, which the room patches also use as their outer cap.
        var baseTimeoutMs = string.Equals(kind, "combat", StringComparison.Ordinal)
            ? CombatRequestTimeoutMs
            : OutOfCombatTimeoutMs;

        for (var round = 0; ; round++)
        {
            var imageForThisRequest = followUpImage;
            followUpImage = null;

            // A tool round may never outlive the decision's own limit.
            var roundTimeoutMs = round > 0 ? Math.Min(toolRoundTimeoutMs, baseTimeoutMs) : (int?)null;

            var response = retryOnFailure
                ? await SendNonCombatDecisionWithRetryAsync(systemPrompt, prompt, maxTokens, temp, history, ct,
                    roundTimeoutMs, imageForThisRequest, baseTimeoutMs)
                : await SendDecisionAsync(systemPrompt, prompt, maxTokens, temp, history, ct,
                    roundTimeoutMs, imageForThisRequest, baseTimeoutMs);

            if (!response.IsSuccess)
                return response;

            var calls = ResponseParser.ExtractToolCalls(response.Content);
            if (calls.Count == 0)
                return response;

            if (HasAnswer(response.Content, hasAnswer))
            {
                MainFile.Logger.Warn("[DecisionService] 响应同时包含决策内容与 tool_calls，忽略工具调用");
                return response;
            }

            if (round >= maxToolRounds)
                return response;

            // The human interrupted mid-decision in solo climb: don't spend another request.
            if (SoloRunController.IsActive && AiFlowControl.IsPaused)
                return response;

            var toolResult = MainThread.Invoke(() => _tools.Execute(aiPlayer, calls));
            LogToolCalls(calls, toolResult.Text);

            history.Add(new AiMessage { Role = "user", Content = prompt });
            history.Add(new AiMessage { Role = "assistant", Content = response.Content });
            prompt = "工具结果：\n" + toolResult.Text +
                     "\n\n请基于上面的工具结果，直接输出最终决策 JSON；不要再次调用工具。";
            followUpImage = toolResult.ImageDataUrl;
        }
    }

    private static bool HasAnswer(string content, Func<System.Text.Json.JsonElement, bool> hasAnswer)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(ResponseParser.ExtractJson(content));
            return hasAnswer(doc.RootElement);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Appends the latest player chat lines to a decision prompt answered by dsh, so a tactic typed
    /// into the chat panel still reaches the decision even when the follow-up message was missed.
    /// 聊天内容进入 dsh 的决策提示词。
    /// </summary>
    private static string AppendRecentChat(string prompt)
    {
        try
        {
            var block = AiTeammateManager.Instance?.ChatManager?.BuildRecentChatBlockForPrompt();
            return string.IsNullOrWhiteSpace(block) ? prompt : prompt + "\n\n" + block;
        }
        catch
        {
            return prompt;
        }
    }

    private static bool HasChoiceKey(System.Text.Json.JsonElement root) => root.TryGetProperty("choice", out _);
    private static bool HasChoicesKey(System.Text.Json.JsonElement root) => root.TryGetProperty("choices", out _);
    private static bool HasIndicesKey(System.Text.Json.JsonElement root) => root.TryGetProperty("indices", out _);

    private static bool HasCombatActions(System.Text.Json.JsonElement root) =>
        root.TryGetProperty("actions", out var actions)
        && actions.ValueKind == System.Text.Json.JsonValueKind.Array
        && actions.GetArrayLength() > 0;

    /// <summary>Out-of-combat decisions get two tool rounds; 0 disables tools for this call.</summary>
    private static int OutOfCombatToolRounds =>
        AiSettingsStore.Current.EnableOutOfCombatReadTools || AiSettingsStore.Current.EnableScreenshotTool
            ? MaxOutOfCombatToolRounds
            : 0;

    private const int MaxOutOfCombatToolRounds = 2;
    private const int MaxOutOfCombatToolTimeoutMs = 15000;

    /// <summary>Base timeout for one combat decision request (setting, clamped). 战斗决策单次请求超时。</summary>
    public static int CombatRequestTimeoutMs =>
        Math.Clamp(AiSettingsStore.Current.DecisionTimeoutMs, 5000, 120000);

    /// <summary>
    /// Upper bound for one out-of-combat decision: the single request timeout <em>and</em> the outer cap
    /// the room patches (rest site / treasure / follower sync) apply around it. Driven by the
    /// "out-of-combat decision limit" setting. 非战斗决策上限时间：单次请求超时 + 房间补丁外层上限。
    /// </summary>
    public static int OutOfCombatTimeoutMs =>
        Math.Clamp(AiSettingsStore.Current.NonCombatDecisionTimeoutMs, 5000, 180000);

    public void ResetCombatHistory(string? combatId = null)
    {
        _conversationContext.ResetCombat();
    }

    // ---- Combat ----

    /// <summary>Extra request rounds per combat decision that may be spent on read-only tool calls.</summary>
    private const int MaxToolRounds = 1;

    /// <summary>Upper bound for the follow-up request after a tool call (shares the 45s turn budget).</summary>
    private const int MaxToolRoundTimeoutMs = 12000;

    public async Task<DecisionResult> DecideCombatAsync(Player aiPlayer, CancellationToken ct)
    {
        var systemPrompt = BuildSystemPrompt(includeTeamPrinciples: true);
        var allowTools = AiSettingsStore.Current.EnableReadTools || AiSettingsStore.Current.EnableScreenshotTool;

        // The prompt reads live combat state and formats card descriptions through the game's
        // localization formatter, which is main-thread-only (shared SmartFormat pools). Build it in a
        // single main-thread hop; only the HTTP round trip runs on the thread pool.
        var firstPrompt = MainThread.Invoke(() =>
        {
            var prelude = BuildContextPrelude(aiPlayer);
            var snapshot = _conversationContext.BuildCombatContext(aiPlayer, _contextBuilder);
            var directiveContext = AiTeammateManager.Instance.DirectiveManager.BuildCombatDirectivePrompt(aiPlayer);
            return BuildCombatDecisionPrompt(prelude, snapshot, directiveContext, allowTools);
        });

        // The tool round is an extra request inside the 45s turn budget: keep it short.
        var toolTimeoutMs = Math.Min(AiSettingsStore.Current.DecisionTimeoutMs, MaxToolRoundTimeoutMs);

        var response = await RequestWithToolsAsync(aiPlayer, systemPrompt, firstPrompt, 1024, 0.3f,
            allowTools ? MaxToolRounds : 0, toolTimeoutMs, HasCombatActions, retryOnFailure: false, ct,
            kind: "combat");

        if (!response.IsSuccess)
            return DecisionResult.Fallback();

        var result = ResponseParser.ParseDecision(response.Content);

        // Tool calls that were never executed (tools disabled, budget spent, or interrupted) are
        // not a plan: end the turn instead of idling with no actions.
        if (result.ToolCalls.Count > 0 && result.Actions.Count == 0 && result.DeferredActions.Count == 0)
            return DecisionResult.Fallback();

        if (!result.IsFallback)
            MainThread.Run(() => _conversationContext.RecordCombatDecision(aiPlayer, _contextBuilder, result));
        return result;
    }

    private static void LogToolCalls(IReadOnlyList<ToolCall> calls, string output)
    {
        var names = string.Join(", ", calls.Select(call => call.Args.Count == 0
            ? call.Name
            : $"{call.Name}({string.Join(",", call.Args.Select(arg => $"{arg.Key}={arg.Value}"))})"));
        MainFile.Logger.Info($"[DecisionService] 工具调用: {names}");

        try
        {
            var manager = AiTeammateManager.Instance;
            if (manager?.AiLog == null)
                return;

            var summary = output.Length > 200 ? output[..200] + "…" : output;
            manager.AiLog.Log("AI_TOOL", $"[工具] {names} -> {summary}");
        }
        catch
        {
            // Tool logging must never break the decision path.
        }
    }

    private string BuildCombatDecisionPrompt(string prelude, string snapshot, string directiveContext, bool allowReadTools)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(prelude))
        {
            sb.AppendLine(prelude);
            sb.AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(directiveContext))
        {
            sb.AppendLine(directiveContext);
            sb.AppendLine();
        }
        sb.AppendLine(snapshot);
        sb.AppendLine();
        sb.AppendLine("请以 JSON 格式返回你的战斗决策（json output required）：");
        sb.AppendLine("```json");
        sb.AppendLine("{\"thinking\":\"战术分析\",\"chat_message\":\"简短沟通(可选)\",\"actions\":[{\"type\":\"play_card\",\"card_name\":\"卡名\",\"target\":\"敌人名或#索引\"},{\"type\":\"use_potion\",\"potion_name\":\"药水名\",\"target\":\"目标\"},{\"type\":\"end_turn\"}],\"deferred_actions\":[{\"type\":\"deferred_attack\",\"card_name\":\"攻击牌名\",\"target\":\"敌人名或#索引\",\"wait_for\":\"等待队友某种行为后打出\"}]}");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("注意: card_name必须精确匹配手牌中的卡名。攻击牌通常必须指定敌人名；若你要给队友打支援牌或给队友用药水，target 可以写玩家名、角色名或“玩家”。");
        sb.AppendLine("【目标写法】敌人行开头的 [#n] 是索引（与快照 combat.enemies[].index 同一个数），可以直接写 target:\"#[n]\"，也可以写 名字(当前血/上限) 精确指定；同名敌人只写名字会因歧义被丢弃（系统不会随便挑一个打）。索引以最新一条快照为准。");
        sb.AppendLine("【药水使用】药水不消耗能量，是免费行动！每回合都应该考虑使用。potion_name必须精确匹配你拥有的药水名称，target 写 Self 表示给自己用。");
        sb.AppendLine("【能量约束】actions 中所有 play_card 的合计费用不能超过你当前的可用能量（含辉星）。deferred_actions 中的攻击牌也会消耗能量，请确保 actions + deferred_actions 总费用 ≤ 当前能量。");
        sb.AppendLine("chat_message: 用角色语气简短沟通，不超过20字。");
        sb.AppendLine("若队友手里有易伤/破甲/力量/补刀机会，优先先出防御、力量、过牌等铺垫牌，把会受益的攻击放进 deferred_actions。");
        sb.AppendLine("若敌人已被队友压到残血，除非你能稳定完成击杀，否则不要抢斩杀；用 chat_message 明确集火目标。");
        sb.AppendLine("若上方存在[玩家战术指令]，应优先考虑执行；尤其是集火目标和给队友上药水。若你认为不该执行，请在 thinking 和 chat_message 中体现理由。");

        // With dsh as the brain the executor no longer auto-plays leftovers, so one turn can hold
        // several decisions — one per mid-turn draw. Tell the agent how the loop behaves.
        if (AgentDecisionBroker.Enabled)
        {
            sb.AppendLine("【外部大脑】本回合不再自动补牌：你的 actions 执行完后，若仍有能量/辉星且手牌里有能打出的牌（包括出牌、药水抽到的牌），系统会把最新局面再交给你决策一次；");
            sb.AppendLine("所以你只需规划眼前这一步。不想再出牌时，务必在 actions 中返回 {\"type\":\"end_turn\"} 结束回合。");
            AppendExternalAgentTools(sb);
        }
        else if (allowReadTools)
        {
            AppendToolInstructions(sb, combat: true);
        }

        return sb.ToString();
    }

    private string BuildContextPrelude(Player aiPlayer)
    {
        var sb = new StringBuilder();
        var profile = _personalityManager.Active;
        var settings = AiSettingsStore.Current;

        if (SoloRunController.IsActive)
        {
            sb.AppendLine("【单人爬塔】你正在独自爬塔，队伍里只有你一个，不需要等待或配合队友；按自己的节奏和判断推进。");
            sb.AppendLine();
        }

        // Long-term memories belong to the mod's personality profile; with dsh driving they would
        // contradict the persona it defines, so they stay out of the prompt. dsh 接管时不注入人格记忆。
        if (profile != null && !AgentDecisionBroker.Enabled
            && settings.EnableLongTermMemory && settings.MaxMemoriesPerPrompt > 0)
        {
            var memory = _memoryManager.Load(profile.Id);
            var memories = _memoryManager.GetRelevantMemories(profile.Id, settings.MaxMemoriesPerPrompt);
            var memoryContext = _promptBuilder.BuildMemoryContext(memories, memory.SelfReflection);
            if (!string.IsNullOrWhiteSpace(memoryContext))
            {
                sb.AppendLine(memoryContext);
                sb.AppendLine();
            }
        }

        var runPrelude = _conversationContext.BuildRunPrelude(aiPlayer, _contextBuilder);
        if (!string.IsNullOrWhiteSpace(runPrelude))
            sb.Append(runPrelude);

        // Notes dsh wrote with sts2_note: a run plan that survives its own context compaction.
        if (AgentDecisionBroker.Enabled)
        {
            var notes = AgentNotes.SnapshotForPrompt();
            if (!string.IsNullOrWhiteSpace(notes))
            {
                sb.AppendLine("[AI 笔记]");
                sb.AppendLine(notes);
                sb.AppendLine();
            }
        }

        return sb.ToString().TrimEnd();
    }

    private string BuildTaskPrompt(Player aiPlayer, string context, string instruction, bool advertiseTools = true)
    {
        var sb = new StringBuilder();
        var prelude = BuildContextPrelude(aiPlayer);
        if (!string.IsNullOrWhiteSpace(prelude))
        {
            sb.AppendLine(prelude);
            sb.AppendLine();
        }

        sb.AppendLine(context);
        sb.AppendLine(instruction);

        if (AgentDecisionBroker.Enabled)
            AppendExternalAgentTools(sb);
        else if (advertiseTools
                 && (AiSettingsStore.Current.EnableOutOfCombatReadTools || AiSettingsStore.Current.EnableScreenshotTool))
            AppendToolInstructions(sb, combat: false);

        return sb.ToString();
    }

    /// <summary>
    /// Tool hints for the external agent. dsh uses native sts2_* tools, so the built-in inline
    /// <c>tool_calls</c> protocol is not advertised: following it would only burn a decision round
    /// (the broker executes those calls and parks a second decision).
    /// </summary>
    private static void AppendExternalAgentTools(StringBuilder sb)
    {
        sb.AppendLine();
        sb.AppendLine("【可用工具】你通过原生 sts2_* 工具行动，不要输出内置的 tool_calls JSON：");
        sb.AppendLine("- sts2_state / sts2_wait：查看状态、等待游戏需要输入；");
        sb.AppendLine("- sts2_read：按需查询遗物、牌堆、单卡、玩家、运行、地图、牌组统计（抽牌堆顺序不可见）；");
        sb.AppendLine("- sts2_note：记录长期计划（构筑方向、Boss 对策、想买的遗物），后续决策会自动带上；");
        sb.AppendLine("- sts2_decide：用本 prompt 要求的确切 JSON 形状提交决策。");
    }

    /// <summary>Advertises the read-only tools and their call format at the end of a prompt.</summary>
    private static void AppendToolInstructions(StringBuilder sb, bool combat)
    {
        var includeScreenshot = AiSettingsStore.Current.EnableScreenshotTool;
        sb.AppendLine();
        if (combat)
        {
            sb.AppendLine("【只读工具（可选）】如果上面的信息不足，你可以先只返回工具调用，不要返回 actions：");
            sb.AppendLine("{\"thinking\":\"为什么要查\",\"tool_calls\":[{\"name\":\"read_relics\"}]}");
            sb.AppendLine(AiToolRunner.DescribeCombatTools(includeScreenshot));
            sb.AppendLine("规则：每次决策最多一轮工具调用；系统会把工具结果发回给你，之后必须直接输出最终决策 JSON，不要再次调用工具。抽牌堆顺序无法获得。");
            sb.AppendLine("多人模式下队友行只给出牌堆张数；需要队友的具体牌时传 player（队友名或角色名），例如 {\"pile\":\"draw\",\"player\":\"队友名\"}。");
            if (includeScreenshot)
                sb.AppendLine("如果你调用了 screenshot，系统会截取当前游戏画面并把图像附在工具结果之后；请结合画面判断，但不要依赖画面读取抽牌堆顺序。");
        }
        else
        {
            sb.AppendLine("【只读工具（可选）】如果上面的信息不足，你可以先只返回工具调用，不要返回决策字段：");
            sb.AppendLine("{\"thinking\":\"为什么要查\",\"tool_calls\":[{\"name\":\"read_map\"}]}");
            sb.AppendLine(AiToolRunner.DescribeOutOfCombatTools(includeScreenshot));
            sb.AppendLine("规则：本决策最多两轮工具调用；收到工具结果后必须直接输出最终决策 JSON，不要再次调用工具。");
        }
    }

    // ---- Card Reward ----

    public async Task<(int choice, string reason)> DecideCardRewardAsync(Player aiPlayer, List<CardModel> choices, CancellationToken ct)
    {
        int count = choices.Count > 0 ? choices.Count : 3;
        var context = _contextBuilder.BuildCardRewardContext(aiPlayer, choices);
        var systemPrompt = BuildSystemPrompt(includeTeamPrinciples: false);
        var userPrompt = BuildTaskPrompt(aiPlayer, context, "请以 JSON 格式返回选牌决策：{\"choice\":0~2选一张, \"reason\":\"理由\"} 或 {\"choice\":-1, \"reason\":\"跳过理由\"}");

        var response = await RequestWithToolsAsync(aiPlayer, systemPrompt, userPrompt, 256, 0.4f,
            OutOfCombatToolRounds, MaxOutOfCombatToolTimeoutMs, HasChoiceKey, retryOnFailure: true, ct,
            kind: "card_reward");

        if (!response.IsSuccess) return (0, "AI不可用，默认选第一张");

        try
        {
            var json = ResponseParser.ExtractJson(response.Content);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var choice = doc.RootElement.GetProperty("choice").GetInt32();
            var reason = doc.RootElement.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";
            return (choice >= 0 && choice < count ? choice : -1, reason);
        }
        catch
        {
            var fallbackChoice = ResponseParser.TryExtractChoice(response.Content) ?? 0;
            var fallbackReason = ResponseParser.TryExtractReason(response.Content);
            return (fallbackChoice >= 0 && fallbackChoice < count ? fallbackChoice : -1, fallbackReason);
        }
    }

    // ---- Map Navigation ----

    /// <summary>
    /// Map choice with an explicit option list (solo climb and multiplayer votes). Returns -1 when
    /// the backend fails so the caller can apply its own heuristic.
    /// </summary>
    public async Task<(int choice, string reason)> DecideMapNavigationAsync(
        Player aiPlayer, IReadOnlyList<string> options, CancellationToken ct)
    {
        if (options.Count == 0)
            return (-1, "");

        var sb = new StringBuilder(_contextBuilder.BuildMapContext(aiPlayer));
        sb.AppendLine("可前往的节点：");
        for (var i = 0; i < options.Count; i++)
            sb.AppendLine($"{i}. {options[i]}");

        var systemPrompt = BuildSystemPrompt(includeTeamPrinciples: false);
        var userPrompt = BuildTaskPrompt(aiPlayer, sb.ToString(),
            "请以 JSON 格式返回选路决策：{\"choice\": 目标索引, \"reason\": \"简短理由\"}");

        var response = await RequestWithToolsAsync(aiPlayer, systemPrompt, userPrompt, 256, 0.4f,
            OutOfCombatToolRounds, MaxOutOfCombatToolTimeoutMs, HasChoiceKey, retryOnFailure: true, ct,
            kind: "map");
        if (!response.IsSuccess)
            return (-1, "AI不可用");

        try
        {
            var json = ResponseParser.ExtractJson(response.Content);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var choice = doc.RootElement.GetProperty("choice").GetInt32();
            var reason = doc.RootElement.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";
            return (choice, reason);
        }
        catch
        {
            return (ResponseParser.TryExtractChoice(response.Content) ?? -1,
                ResponseParser.TryExtractReason(response.Content));
        }
    }

    // ---- Rest Site ----

    public async Task<RestSiteDecisionResult> DecideRestSiteAsync(Player aiPlayer, int currentHp, int maxHp, IReadOnlyList<MegaCrit.Sts2.Core.Entities.RestSite.RestSiteOption> options, CancellationToken ct)
    {
        var optionIds = options.Select(o => o.OptionId).ToList();
        var context = _contextBuilder.BuildRestSiteContext(aiPlayer, currentHp, maxHp, options);
        var systemPrompt = BuildSystemPrompt(includeTeamPrinciples: false);
        var smithOption = options.OfType<MegaCrit.Sts2.Core.Entities.RestSite.SmithRestSiteOption>().FirstOrDefault();
        var upgradableCards = aiPlayer.Deck.Cards.Where(c => c.IsUpgradable).ToList();
        var smithCount = smithOption?.SmithCount ?? 0;

        var instruction = new StringBuilder();
        instruction.AppendLine($"请以 JSON 格式返回休息决策：{{\"choice\":0~{optionIds.Count - 1}, \"reason\":\"...\", \"upgrade_indices\":[仅在选择SMITH时填写升级卡牌索引]}}");
        if (smithOption?.IsEnabled == true)
            instruction.AppendLine($"如果 choice 对应 SMITH，upgrade_indices 必须从上面的可升级卡牌列表中选择 1~{smithCount} 个不重复索引；否则返回空数组。");

        var response = await RequestWithToolsAsync(aiPlayer, systemPrompt,
            BuildTaskPrompt(aiPlayer, context, instruction.ToString().TrimEnd()), 256, 0.3f,
            OutOfCombatToolRounds, MaxOutOfCombatToolTimeoutMs, HasChoiceKey, retryOnFailure: true, ct,
            kind: "rest");

        int healIdx = optionIds.IndexOf("HEAL");
        int smithIdx = optionIds.IndexOf("SMITH");
        int digIdx = optionIds.IndexOf("DIG");
        int mendIdx = optionIds.IndexOf("MEND");
        int liftIdx = optionIds.IndexOf("LIFT");

        if (healIdx < 0) healIdx = 0;
        if (smithIdx < 0) smithIdx = optionIds.Count > 1 ? 1 : 0;

        var hpPct = maxHp > 0 ? (currentHp * 100 / maxHp) : 100;
        int safeFallback;
        if (hpPct <= 20) safeFallback = healIdx;
        else if (hpPct <= 35) safeFallback = mendIdx >= 0 ? mendIdx : healIdx;
        else if (hpPct <= 50) safeFallback = healIdx;
        else safeFallback = smithIdx;

        if (!response.IsSuccess)
            return new RestSiteDecisionResult
            {
                Choice = safeFallback,
                Reason = $"AI不可用，HP{hpPct}%使用安全策略"
            };

        try
        {
            var json = ResponseParser.ExtractJson(response.Content);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var choice = doc.RootElement.GetProperty("choice").GetInt32();
            var reason = doc.RootElement.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";
            var upgradeIndices = ParseUpgradeIndices(doc.RootElement, upgradableCards.Count, smithCount);

            if (choice < 0 || choice >= optionIds.Count) choice = safeFallback;

            // If AI chose MEND but no alive injured teammate, fall back to safe choice
            if (choice == mendIdx)
            {
                var teammates = aiPlayer.RunState?.Players.Where(p => p != aiPlayer).ToList() ?? new();
                var aliveInjured = teammates.Any(t => t.Creature?.IsAlive == true
                    && (t.Creature?.CurrentHp ?? 0) < (t.Creature?.MaxHp ?? 1));
                if (!aliveInjured)
                {
                    MainFile.Logger.Info("AI chose MEND but no injured teammates, falling back to safe option");
                    return new RestSiteDecisionResult { Choice = safeFallback, Reason = "没有需要治疗的队友，使用安全策略" };
                }
            }

            // Only override SMITH when HP is critically low (≤20%) — at 20-35% LLM can still
            // reasonably choose SMITH, especially with good upgrade targets.
            if (hpPct <= 20 && choice != healIdx && (choice == smithIdx || choice == digIdx || choice == liftIdx))
                return new RestSiteDecisionResult { Choice = healIdx, Reason = $"HP仅{hpPct}%危急，强制休息保命" };

            // Removed: overriding SMITH→MEND at ≤35% HP. MEND heals allies, not the AI player,
            // so it's worse than SMITH when the AI itself is dying.

            if (choice != smithIdx)
                upgradeIndices.Clear();

            var choiceLabel = choice >= 0 && choice < options.Count ? SafeOptionLabel(options[choice]) : $"索引{choice}";

            return new RestSiteDecisionResult
            {
                Choice = choice,
                Reason = reason,
                UpgradeIndices = upgradeIndices
            };
        }
        catch
        {
            var fallbackChoice = ResponseParser.TryExtractChoice(response.Content);
            if (fallbackChoice.HasValue && fallbackChoice.Value >= 0 && fallbackChoice.Value < optionIds.Count)
            {
                var fallbackReason = ResponseParser.TryExtractReason(response.Content);
                return new RestSiteDecisionResult
                {
                    Choice = fallbackChoice.Value,
                    Reason = fallbackReason.Length > 0 ? fallbackReason : "LLM解析回退"
                };
            }
            return new RestSiteDecisionResult
            {
                Choice = safeFallback,
                Reason = "解析失败，使用HP安全策略"
            };
        }
    }

    private static List<int> ParseUpgradeIndices(System.Text.Json.JsonElement root, int cardCount, int smithCount)
    {
        var indices = new List<int>();
        if (smithCount <= 0 || cardCount <= 0) return indices;
        if (!root.TryGetProperty("upgrade_indices", out var arr) || arr.ValueKind != System.Text.Json.JsonValueKind.Array)
            return indices;

        foreach (var elem in arr.EnumerateArray())
        {
            if (elem.ValueKind != System.Text.Json.JsonValueKind.Number) continue;
            var idx = elem.GetInt32();
            if (idx >= 0 && idx < cardCount && !indices.Contains(idx))
                indices.Add(idx);
        }

        if (indices.Count > smithCount)
            indices = indices.Take(smithCount).ToList();

        return indices;
    }

    private static string SafeOptionLabel(MegaCrit.Sts2.Core.Entities.RestSite.RestSiteOption option)
    {
        var title = PromptTextHelper.ResolveLocString(option.Title);
        return string.IsNullOrWhiteSpace(title) ? option.OptionId : title;
    }

    // ---- Shop ----

    public async Task<(List<int> choices, string reason)> DecideShopAsync(Player aiPlayer, List<ShopDecisionEntry> entries, CancellationToken ct)
    {
        var context = _contextBuilder.BuildShopContext(aiPlayer, entries);
        var systemPrompt = BuildSystemPrompt(includeTeamPrinciples: false);
        var instruction = $"请以 JSON 格式返回购物决策：{{\"choices\":[索引列表], \"reason\":\"理由\"}}\nchoices 为要购买的物品索引数组（可多选），留空 [] 表示离开商店。索引范围 0~{entries.Count - 1}。";

        var response = await RequestWithToolsAsync(aiPlayer, systemPrompt,
            BuildTaskPrompt(aiPlayer, context, instruction), 384, 0.3f,
            OutOfCombatToolRounds, MaxOutOfCombatToolTimeoutMs, HasChoicesKey, retryOnFailure: true, ct,
            kind: "shop");

        if (!response.IsSuccess) return (new List<int>(), "AI不可用，跳过购物");

        try
        {
            var json = ResponseParser.ExtractJson(response.Content);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var reason = doc.RootElement.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";

            var choices = new List<int>();
            if (doc.RootElement.TryGetProperty("choices", out var arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var elem in arr.EnumerateArray())
                {
                    if (elem.ValueKind == System.Text.Json.JsonValueKind.Number)
                    {
                        var idx = elem.GetInt32();
                        if (idx >= 0 && idx < entries.Count && !choices.Contains(idx))
                            choices.Add(idx);
                    }
                }
            }

            return (choices, reason);
        }
        catch
        {
            // Fallback: try old single-choice format
            var fallbackChoice = ResponseParser.TryExtractChoice(response.Content);
            var fallbackReason = ResponseParser.TryExtractReason(response.Content);
            if (fallbackChoice.HasValue && fallbackChoice.Value >= 0 && fallbackChoice.Value < entries.Count)
                return (new List<int> { fallbackChoice.Value }, fallbackReason);
            if (fallbackChoice.HasValue && fallbackChoice.Value < 0)
                return (new List<int>(), fallbackReason);
            return (new List<int>(), "解析失败");
        }
    }

    // ---- Generic Card Selection (enchant, remove, transform, upgrade) ----

    public async Task<(List<int> indices, string reason)> DecideCardSelectionAsync(
        Player aiPlayer, List<CardModel> cards, int minSelect, int maxSelect,
        string selectionType, CancellationToken ct)
    {
        if (cards.Count == 0) return (new List<int>(), "无牌可选");

        var context = BuildCardSelectionContext(aiPlayer, cards, selectionType);
        var systemPrompt = BuildSystemPrompt(includeTeamPrinciples: false);
        var response = await RequestWithToolsAsync(
            aiPlayer,
            systemPrompt,
            BuildTaskPrompt(aiPlayer, context, $"请以 JSON 格式返回选牌决策：{{\"indices\":[...], \"reason\":\"理由\"}} 选{minSelect}~{maxSelect}张"),
            1024,
            0.4f,
            OutOfCombatToolRounds,
            MaxOutOfCombatToolTimeoutMs,
            HasIndicesKey,
            retryOnFailure: true,
            ct,
            kind: "card_selection");

        if (!response.IsSuccess)
            return (new List<int>(), "AI不可用");

        try
        {
            var json = ResponseParser.ExtractJson(response.Content);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var indicesJson = doc.RootElement.GetProperty("indices");
            var indices = new List<int>();
            foreach (var elem in indicesJson.EnumerateArray())
            {
                var idx = elem.GetInt32();
                if (idx >= 0 && idx < cards.Count && !indices.Contains(idx))
                    indices.Add(idx);
            }
            // Clamp to min/max
            if (indices.Count < minSelect)
            {
                for (int i = 0; i < cards.Count && indices.Count < minSelect; i++)
                    if (!indices.Contains(i)) indices.Add(i);
            }
            if (indices.Count > maxSelect)
                indices = indices.Take(maxSelect).ToList();

            var reason = doc.RootElement.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";
            return (indices, reason);
        }
        catch
        {
            // Regex fallback: extract first valid index from "indices"
            var fallbackReason = ResponseParser.TryExtractReason(response.Content);
            var fallbackIndices = new List<int>();
            // Try to extract individual indices from malformed JSON
            var matches = System.Text.RegularExpressions.Regex.Matches(
                ResponseParser.ExtractJson(response.Content), @"(-?\d+)");
            foreach (System.Text.RegularExpressions.Match m in matches)
            {
                if (int.TryParse(m.Groups[1].Value, out var idx) && idx >= 0 && idx < cards.Count && !fallbackIndices.Contains(idx))
                    fallbackIndices.Add(idx);
            }
            if (fallbackIndices.Count < minSelect)
            {
                for (int i = 0; i < cards.Count && fallbackIndices.Count < minSelect; i++)
                    if (!fallbackIndices.Contains(i)) fallbackIndices.Add(i);
            }
            if (fallbackIndices.Count > maxSelect)
                fallbackIndices = fallbackIndices.Take(maxSelect).ToList();
            return (fallbackIndices, fallbackReason.Length > 0 ? fallbackReason : "解析失败，选择前几张");
        }
    }

    private string BuildCardSelectionContext(Player aiPlayer, List<CardModel> cards, string selectionType)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[{selectionType}] 选{cards.Count}张");

        var hp = aiPlayer.Creature?.CurrentHp ?? 0;
        var maxHp = aiPlayer.Creature?.MaxHp ?? 0;
        sb.Append($"{hp}/{maxHp}HP Gold:{aiPlayer.Gold}");

        var deckCards = aiPlayer.Deck?.Cards;
        if (deckCards != null && deckCards.Any())
            sb.Append($" | 牌组:{deckCards.Count}张");

        sb.AppendLine();
        for (int i = 0; i < cards.Count; i++)
        {
            var c = cards[i];
            var ol = _contextBuilder.GetCardOneLiner(c, 50);
            sb.AppendLine($"{i}. {c.Title}({c.Rarity}) {ol}");
        }

        var exactKnowledge = _contextBuilder.EnrichContext(selectionType);
        var cardKnowledge = cards.Count <= 10 ? string.Join(Environment.NewLine, cards.Select(c => _contextBuilder.GetCardOneLiner(c, 80))) : "";
        if (!string.IsNullOrWhiteSpace(cardKnowledge))
        {
            sb.AppendLine();
            sb.AppendLine("[候选卡牌资料]");
            for (int i = 0; i < cards.Count; i++)
                sb.AppendLine($"{i}. {_contextBuilder.GetCardOneLiner(cards[i], 80)}");
        }

        // Keep mention-based lookup for selection types that explicitly reference relics or enchantments.
        var enrichment = exactKnowledge;
        if (enrichment.Length > 0)
        {
            sb.AppendLine();
            sb.Append("[相关资料]");
            sb.Append(enrichment);
        }

        return sb.ToString();
    }

    // ---- Event ----

    public async Task<(int choice, string reason)> DecideEventAsync(Player aiPlayer, List<string> optionDescs, CancellationToken ct)
    {
        var context = _contextBuilder.BuildEventContext(aiPlayer, optionDescs);
        var systemPrompt = BuildSystemPrompt(includeTeamPrinciples: false);
        var response = await RequestWithToolsAsync(
            aiPlayer,
            systemPrompt,
            BuildTaskPrompt(aiPlayer, context, $"请以 JSON 格式返回事件决策：{{\"choice\":0~{optionDescs.Count - 1}, \"reason\":\"理由\"}}"),
            1024,
            0.3f,
            OutOfCombatToolRounds,
            MaxOutOfCombatToolTimeoutMs,
            HasChoiceKey,
            retryOnFailure: true,
            ct,
            kind: "event");

        if (!response.IsSuccess) return (0, "AI不可用，默认选第一个");

        try
        {
            var json = ResponseParser.ExtractJson(response.Content);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var choice = doc.RootElement.GetProperty("choice").GetInt32();
            var reason = doc.RootElement.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";
            var valid = choice >= 0 && choice < optionDescs.Count ? choice : 0;
            return (valid, reason);
        }
        catch
        {
            var fallbackChoice = ResponseParser.TryExtractChoice(response.Content) ?? 0;
            var fallbackReason = ResponseParser.TryExtractReason(response.Content);
            var valid = fallbackChoice >= 0 && fallbackChoice < optionDescs.Count ? fallbackChoice : 0;
            return (valid, fallbackReason);
        }
    }

    // ---- Treasure relic ----

    /// <summary>
    /// Treasure chest: every player votes on the same relic pool, so this is the AI's own vote (host
    /// only — peers receive it as a <c>PickRelicAction</c>). Returns -1 when nothing usable came back
    /// so the caller can apply its local fallback. Read tools are not advertised: the whole chest
    /// context is already in the prompt, and the other players are waiting at the opened chest.
    /// 宝箱遗物投票：候选题目已在提示词里，不再叠工具轮；失败返回 -1 交给本地兜底。
    /// </summary>
    public async Task<(int choice, string reason)> DecideTreasureRelicAsync(
        Player aiPlayer,
        IReadOnlyList<RelicModel> relics,
        string? teammateVoteNote,
        CancellationToken ct)
    {
        if (relics.Count == 0)
            return (-1, "");

        var instruction = new StringBuilder();
        instruction.AppendLine($"请以 JSON 格式返回宝箱遗物选择：{{\"choice\":0~{relics.Count - 1}, \"reason\":\"理由\"}}");
        instruction.AppendLine("宝箱遗物必须选一个，不能跳过。按自己角色的构筑、当前牌组和已有遗物评估哪个最有价值；");
        instruction.AppendLine("这是给自己的遗物，不是团队分配：优先考虑自己能不能用得上。");
        instruction.AppendLine("多人模式下所有玩家对同一组遗物投票：多人投同一个遗物时靠猜拳决定归属，输家拿剩下的遗物；");
        instruction.AppendLine("若队友已经投了某个你很想要的遗物，可以权衡要不要改投自己第二想要、且没人抢的那个。");

        // Build on the main thread: the prompt formats localized relic descriptions (SmartFormat
        // pools are main-thread-only), mirroring DecideCombatAsync.
        var userPrompt = MainThread.Invoke(() =>
        {
            var context = _contextBuilder.BuildTreasureRelicContext(aiPlayer, relics, teammateVoteNote);
            return BuildTaskPrompt(aiPlayer, context, instruction.ToString().TrimEnd(), advertiseTools: false);
        });

        var response = await RequestWithToolsAsync(
            aiPlayer,
            BuildSystemPrompt(includeTeamPrinciples: false),
            userPrompt,
            256,
            0.3f,
            0,
            MaxOutOfCombatToolTimeoutMs,
            HasChoiceKey,
            false,
            ct,
            kind: "treasure_relic");

        if (!response.IsSuccess)
            return (-1, "AI不可用");

        try
        {
            var json = ResponseParser.ExtractJson(response.Content);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var choice = doc.RootElement.GetProperty("choice").GetInt32();
            var reason = doc.RootElement.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";
            return (choice >= 0 && choice < relics.Count ? choice : -1, reason);
        }
        catch
        {
            var fallbackChoice = ResponseParser.TryExtractChoice(response.Content) ?? -1;
            var fallbackReason = ResponseParser.TryExtractReason(response.Content);
            return (fallbackChoice >= 0 && fallbackChoice < relics.Count ? fallbackChoice : -1, fallbackReason);
        }
    }

    // ---- Hextech Runes ----

    public async Task<(int choice, List<int>? reroll, string reason)> DecideHextechRuneAsync(
        Player aiPlayer,
        List<RuneOptionInfo> options,
        MonsterHexInfo? monsterHex,
        CancellationToken ct,
        bool allowReroll = true)
    {
        var context = _contextBuilder.BuildHextechRuneContext(aiPlayer, options, monsterHex);
        var systemPrompt = BuildSystemPrompt(includeTeamPrinciples: false);

        var instruction = new StringBuilder();
        instruction.AppendLine("请以 JSON 格式返回海克斯符文选择：");
        instruction.Append("{\"thinking\":\"分析\",\"choice\":0~");
        instruction.Append(options.Count - 1);
        instruction.Append(",\"reason\":\"理由\"");
        if (allowReroll)
            instruction.Append(",\"reroll\":[槽位索引数组或null]");
        instruction.AppendLine("}");
        if (allowReroll)
            instruction.AppendLine("如果三个选项都不理想，reroll 可指定要重投的槽位索引（如 [0,2]），重投后会给你新选项再选一次。");

        var response = await RequestWithToolsAsync(
            aiPlayer,
            systemPrompt,
            BuildTaskPrompt(aiPlayer, context, instruction.ToString().TrimEnd()),
            512,
            0.3f,
            OutOfCombatToolRounds,
            MaxOutOfCombatToolTimeoutMs,
            HasChoiceKey,
            retryOnFailure: true,
            ct,
            kind: "hextech_rune");

        if (!response.IsSuccess)
            return (HextechRunesDecisionHandler.PickHeuristicFallback(aiPlayer, options), null, "AI不可用，使用启发式选择");

        try
        {
            var json = ResponseParser.ExtractJson(response.Content);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            var thinking = root.TryGetProperty("thinking", out var th) ? th.GetString() ?? "" : "";
            if (!string.IsNullOrWhiteSpace(thinking))
                AiTeammateManager.Instance.AiLog.Log("AI_THINK", $"[海克斯符文思考] {thinking}");

            var choice = root.TryGetProperty("choice", out var ch) && ch.ValueKind == System.Text.Json.JsonValueKind.Number
                ? ch.GetInt32() : -1;
            var reason = root.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";

            List<int>? reroll = null;
            if (allowReroll && root.TryGetProperty("reroll", out var rerollArr) &&
                rerollArr.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                reroll = new List<int>();
                foreach (var elem in rerollArr.EnumerateArray())
                {
                    if (elem.ValueKind == System.Text.Json.JsonValueKind.Number)
                    {
                        var idx = elem.GetInt32();
                        if (idx >= 0 && idx < options.Count && !reroll.Contains(idx))
                            reroll.Add(idx);
                    }
                }
                if (reroll.Count == 0) reroll = null;
            }

            if (choice < 0 || choice >= options.Count)
            {
                if (reroll != null && reroll.Count > 0)
                    return (-1, reroll, reason);
                return (HextechRunesDecisionHandler.PickHeuristicFallback(aiPlayer, options), null, reason);
            }

            return (choice, reroll, reason);
        }
        catch
        {
            var fallbackChoice = HextechRunesDecisionHandler.PickHeuristicFallback(aiPlayer, options);
            return (fallbackChoice, null, "解析失败，使用启发式选择");
        }
    }
}

public class DecisionResult
{
    public string Thinking { get; set; } = "";
    public string ChatMessage { get; set; } = "";
    public List<DecisionAction> Actions { get; set; } = new();
    public List<DecisionAction> DeferredActions { get; set; } = new();

    /// <summary>Read-only tool calls the model asked for instead of acting. 模型请求的工具调用。</summary>
    public List<ToolCall> ToolCalls { get; set; } = new();

    public bool IsFallback { get; set; }

    /// <summary>
    /// true = 这份计划来自外部大脑(dsh)：按模型给的顺序出牌、跳过 Phase 3 的“清空剩余手牌”、
    /// 每回合允许多轮决策。多人局里它必须随战斗计划一起广播（<see cref="Compat.AiCombatPlanCodec"/>），
    /// 否则房主和客户端会用不同的执行模式播放同一份计划，AI 弃牌堆顺序随之分叉。
    /// </summary>
    public bool ExternalBrain { get; set; }

    public static DecisionResult Fallback()
    {
        return new DecisionResult
        {
            Thinking = "Fallback: 无法获取 AI 决策，使用默认行为",
            Actions = new List<DecisionAction> { new() { Type = "end_turn" } },
            IsFallback = true
        };
    }
}

public class RestSiteDecisionResult
{
    public int Choice { get; set; }
    public string Reason { get; set; } = "";
    public List<int> UpgradeIndices { get; set; } = new();
}

public class DecisionAction
{
    public string Type { get; set; } = "";
    public string CardName { get; set; } = "";
    public string PotionName { get; set; } = "";
    public string Target { get; set; } = "";
    public string Player { get; set; } = "";
    public string Reason { get; set; } = "";
    public string WaitFor { get; set; } = "";

    /// <summary>
    /// The creature <see cref="Target"/> resolved to when the plan was validated. The executor plays
    /// this exact instance instead of re-resolving the string, so a mid-pass kill that shifts the enemy
    /// list cannot retarget the card (and two same-named enemies cannot be swapped).
    /// 校验阶段锁定的目标实例，执行时直接用它，避免中途死亡导致索引漂移。
    /// </summary>
    public Creature? ResolvedTarget { get; set; }
}
