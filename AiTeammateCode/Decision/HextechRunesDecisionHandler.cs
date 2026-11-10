using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using AiTeammate.AiTeammateCode.Compat;
using AiTeammate.AiTeammateCode.Solo;

namespace AiTeammate.AiTeammateCode.Decision;

/// <summary>
/// Soft-dependency bridge to HextechRunes (ARAM: Mayhem, assembly <c>HextechRunes</c>).
///
/// The current Hextech build has no AI/teammate awareness: every act, forge and ack transaction parks a
/// per-player <see cref="PlayerChoiceSynchronizer"/> wait, and an AI player has no network peer, so the act
/// transition would block forever. The bridge answers those waits for AI players instead:
/// * rune selection / act-selection acks are fed into the local synchronizer, so Hextech's own
///   <c>ResolveRemoteRuneChoice</c> path keeps doing the journaling, seen history and relic grant;
/// * forge / relic-option choices are resolved locally (all peers agree because the pick is seed-stable);
/// * solo climbs answer the selection screen when the AI drives the local player.
///
/// Everything is reflection-based; nothing here is referenced at compile time.
/// </summary>
public static class HextechRunesDecisionHandler
{
    private const string HextechAssemblyName = "HextechRunes";
    private const string CoordinatorTypeName = "HextechRunes.HextechRuneSelectionCoordinator";
    private const string CodecTypeName = "HextechRunes.HextechChoiceCodec";
    private const string StableRandomTypeName = "HextechRunes.HextechStableRandom";

    /// <summary>Bounded by the "out-of-combat decision limit" setting (default 30s).</summary>
    private static int LlmTimeoutMs => DecisionService.OutOfCombatTimeoutMs;

    private static readonly object Gate = new();
    private static volatile bool _isAvailable;
    private static Assembly? _assembly;

    private static MethodInfo? _createRuneSelection;
    private static MethodInfo? _stableIndex;

    private static RunState? _answerRun;
    private static readonly HashSet<(ulong NetId, uint ChoiceId)> AnsweredChoices = new();
    private static readonly HashSet<object> AutoAnsweredScreens = new(ReferenceEqualityComparer.Instance);

    /// <summary>Whether a compatible HextechRunes build is loaded. Re-checks until found.</summary>
    public static bool IsAvailable
    {
        get
        {
            if (_isAvailable) return true;
            lock (Gate)
            {
                if (_isAvailable) return true;
                return Detect();
            }
        }
    }

    /// <summary>Resolves a type from the HextechRunes assembly, or null when it is not loaded.</summary>
    public static Type? FindHextechType(string typeName)
    {
        if (!IsAvailable) return null;
        try { return _assembly!.GetType(typeName, throwOnError: false); }
        catch { return null; }
    }

    private static bool Detect()
    {
        try
        {
            var assembly = FindHextechAssembly();
            if (assembly == null) return false;
            if (assembly.GetType(CoordinatorTypeName, throwOnError: false) == null) return false;
            var codec = assembly.GetType(CodecTypeName, throwOnError: false);
            if (codec == null) return false;

            _createRuneSelection = codec.GetMethod("CreateRuneSelection",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (_createRuneSelection == null) return false;

            var stableRandom = assembly.GetType(StableRandomTypeName, throwOnError: false);
            _stableIndex = stableRandom?.GetMethod("Index",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

            _assembly = assembly;
            _isAvailable = true;
            MainFile.Logger.Info($"[HextechRunes] 检测到 HextechRunes (ARAM: Mayhem) {assembly.GetName().Version}");
            return true;
        }
        catch (Exception ex)
        {
            MainFile.Logger.Info($"[HextechRunes] 检测失败: {ex.Message}");
            return false;
        }
    }

    private static Assembly? FindHextechAssembly()
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.GetName().Name == HextechAssemblyName)
                return assembly;
        }

        return Type.GetType("HextechRunes.HextechRuneSelectionCoordinator, " + HextechAssemblyName,
            throwOnError: false)?.Assembly;
    }

    // ---- Options ----

    /// <summary>Converts rune models into prompt options (localized title + cleaned description).</summary>
    public static List<RuneOptionInfo> ExtractOptions(IReadOnlyList<RelicModel> relics)
    {
        var options = new List<RuneOptionInfo>(relics.Count);
        for (int i = 0; i < relics.Count; i++)
        {
            var relic = relics[i];
            options.Add(new RuneOptionInfo
            {
                Index = i,
                Name = PromptTextHelper.ResolveLocString(relic.Title),
                Description = CleanDescription(PromptTextHelper.ResolveLocString(relic.DynamicDescription)),
                RelicId = (relic.CanonicalInstance?.Id ?? relic.Id).Entry
            });
        }

        return options;
    }

    // ---- AI decisions ----

    /// <summary>
    /// Answers Hextech's remote-choice wait for an AI player in the act rune-selection flow.
    /// The original method keeps waiting; this feeds the payload into the local synchronizer, so the
    /// normal remote branch (journal, seen history, relic grant) resolves it.
    /// </summary>
    public static void AnswerRuneChoiceForAi(
        Player aiPlayer,
        IReadOnlyList<RelicModel> options,
        int actIndex,
        int choiceOrdinal,
        uint choiceId,
        PlayerChoiceSynchronizer? synchronizer)
    {
        if (synchronizer == null || options == null || options.Count == 0)
            return;
        if (!TryBeginAnswer(aiPlayer, choiceId))
            return;

        _ = AnswerRuneChoiceAsync(aiPlayer, options, actIndex, choiceOrdinal, choiceId, synchronizer);
    }

    private static async Task AnswerRuneChoiceAsync(
        Player aiPlayer,
        IReadOnlyList<RelicModel> options,
        int actIndex,
        int choiceOrdinal,
        uint choiceId,
        PlayerChoiceSynchronizer synchronizer)
    {
        int index = 0;
        try
        {
            var infos = ExtractOptions(options);
            index = await DecideWithDeadlineAsync(aiPlayer, infos, actIndex, choiceOrdinal, null);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[HextechRunes] AI 符文决策失败，改用保底选择: {ex.Message}");
        }

        if (index < 0 || index >= options.Count)
            index = Math.Clamp(index, 0, options.Count - 1);

        try
        {
            var finalIndex = index;
            MainThread.Run(() =>
            {
                var payload = BuildRuneSelection(actIndex, choiceOrdinal, finalIndex, options);
                if (payload == null)
                {
                    MainFile.Logger.Warn($"[HextechRunes] 无法构造符文选择载荷 act={actIndex} ordinal={choiceOrdinal}");
                    return;
                }

                synchronizer.ReceiveReplayChoice(aiPlayer, choiceId, payload.ToNetData());
                var name = options[finalIndex].Title;
                AiTeammateManager.Instance.AiLog.Log("AI_ACTION",
                    $"[海克斯符文] 选择 {finalIndex}: {PromptTextHelper.ResolveLocString(name)} | act={actIndex}");
                MainFile.Logger.Info($"[HextechRunes] AI 符文选择已回填 act={actIndex} ordinal={choiceOrdinal} player={aiPlayer.NetId} index={finalIndex}");
            });
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[HextechRunes] 回填符文选择失败 act={actIndex}: {ex.Message}");
        }
    }

    /// <summary>
    /// Resolves a synced forge / relic-option choice for an AI player locally. Every peer runs the same
    /// seed-stable pick, so no message is exchanged; the choice id is still reserved to keep the
    /// per-player sequence in lockstep with every other choice.
    /// </summary>
    public static async Task<RelicModel?> SelectRelicForAiAsync(
        Player aiPlayer,
        IReadOnlyList<RelicModel> options,
        string context)
    {
        if (options == null || options.Count == 0)
            return null;

        try
        {
            RunManager.Instance?.PlayerChoiceSynchronizer?.ReserveChoiceId(aiPlayer);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[HextechRunes] 预留选择ID失败: {ex.Message}");
        }

        int index = 0;
        try
        {
            var infos = ExtractOptions(options);
            var salt = string.IsNullOrEmpty(context) ? "relic-option" : context;
            index = await DecideWithDeadlineAsync(aiPlayer, infos, -1, -1, salt);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[HextechRunes] AI 遗物选择失败，改用保底选择: {ex.Message}");
        }

        index = Math.Clamp(index, 0, options.Count - 1);
        MainFile.Logger.Info($"[HextechRunes] AI 选择遗物 index={index} context={context}");
        return options[index];
    }

    /// <summary>
    /// Decides with a hard wall-clock deadline. A backend that ignores the cancellation token must never
    /// stall the act transition (every other player is waiting on this answer), so the stable pick wins.
    /// </summary>
    private static async Task<int> DecideWithDeadlineAsync(
        Player aiPlayer,
        List<RuneOptionInfo> options,
        int actIndex,
        int choiceOrdinal,
        string? saltSuffix)
    {
        var decideTask = DecideIndexAsync(aiPlayer, options, actIndex, choiceOrdinal, saltSuffix);
        var deadline = Task.Delay(TimeSpan.FromMilliseconds(LlmTimeoutMs + 5_000));
        if (await Task.WhenAny(decideTask, deadline) == decideTask)
            return await decideTask;

        _ = decideTask.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        MainFile.Logger.Warn($"[HextechRunes] AI 决策超过 {(LlmTimeoutMs + 5_000) / 1000.0:F0}s，改用稳定选择");
        return PickDeterministic(aiPlayer, options, aiPlayer.RunState as RunState,
            actIndex, choiceOrdinal, saltSuffix);
    }

    private static async Task<int> DecideIndexAsync(
        Player aiPlayer,
        List<RuneOptionInfo> options,
        int actIndex,
        int choiceOrdinal,
        string? saltSuffix = null)
    {
        if (options.Count == 0) return -1;

        var runState = aiPlayer.RunState as RunState;
        if (CanUseLlm())
        {
            var manager = AiTeammateManager.Instance;
            if (manager.IsInitialized)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(LlmTimeoutMs));
                    var (choice, _, reason) = await manager.DecisionService.DecideHextechRuneAsync(
                        aiPlayer, options, null, cts.Token, allowReroll: false);

                    if (choice >= 0 && choice < options.Count)
                    {
                        if (!string.IsNullOrWhiteSpace(reason))
                            manager.AiLog.Log("AI_THINK", $"[海克斯符文] {reason}");
                        return choice;
                    }
                }
                catch (OperationCanceledException)
                {
                    MainFile.Logger.Warn("[HextechRunes] AI 符文决策超时，改用稳定随机");
                }
                catch (Exception ex)
                {
                    MainFile.Logger.Warn($"[HextechRunes] AI 符文决策异常: {ex.Message}");
                }
            }
        }
        else if (saltSuffix == null)
        {
            MainFile.Logger.Info("[HextechRunes] 多人对局中 AI 改用种子稳定选择（各端必须一致，不调用 LLM）");
        }

        return PickDeterministic(aiPlayer, options, runState, actIndex, choiceOrdinal, saltSuffix);
    }

    /// <summary>
    /// True only when this run is simulated on a single machine, so an LLM pick (non-deterministic) cannot
    /// ever diverge from another peer. With two or more humans every client simulates the AI and must
    /// agree, so the pick falls back to a seed-stable choice.
    /// </summary>
    private static bool CanUseLlm()
    {
        try
        {
            var runState = RunManager.Instance?.DebugOnlyGetState();
            if (runState == null) return true;
            int humans = runState.Players.Count(p => !AiIdentity.IsAi(p.NetId));
            return humans <= 1;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Seed-stable pick shared by every client: best-scored candidates, tie-broken by Hextech's own hash.</summary>
    private static int PickDeterministic(
        Player aiPlayer,
        List<RuneOptionInfo> options,
        RunState? runState,
        int actIndex,
        int choiceOrdinal,
        string? saltSuffix)
    {
        var scores = new int[options.Count];
        for (int i = 0; i < options.Count; i++)
            scores[i] = ScoreOption(aiPlayer, options[i]);

        int best = scores.Max();
        var candidates = new List<int>();
        for (int i = 0; i < options.Count; i++)
        {
            if (scores[i] == best)
                candidates.Add(i);
        }

        if (candidates.Count == 1 || runState == null)
            return candidates[0];

        int slot = StableIndex(runState, candidates.Count,
            "ai-teammate",
            aiPlayer.NetId.ToString(CultureInfo.InvariantCulture),
            actIndex.ToString(CultureInfo.InvariantCulture),
            choiceOrdinal.ToString(CultureInfo.InvariantCulture),
            saltSuffix ?? "");
        return candidates[slot >= 0 ? slot : 0];
    }

    private static int StableIndex(RunState runState, int count, params string?[] salt)
    {
        var index = _stableIndex;
        if (index == null || count <= 0) return 0;
        try
        {
            var result = index.Invoke(null, new object?[] { runState, count, salt });
            return result is int value && value >= 0 && value < count ? value : 0;
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[HextechRunes] 稳定随机失败: {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// Heuristic fallback (deterministic): character-pool match and rarity keywords first, then the
    /// lexicographically smallest relic id so repeated calls agree.
    /// </summary>
    public static int PickHeuristicFallback(Player aiPlayer, List<RuneOptionInfo> options)
    {
        if (options == null || options.Count == 0) return -1;

        int best = 0;
        int bestScore = int.MinValue;
        string bestId = "";

        for (int i = 0; i < options.Count; i++)
        {
            int score = ScoreOption(aiPlayer, options[i]);
            string id = options[i].RelicId ?? "";
            if (score > bestScore
                || (score == bestScore && string.CompareOrdinal(id, bestId) < 0))
            {
                bestScore = score;
                best = i;
                bestId = id;
            }
        }

        MainFile.Logger.Info($"[HextechRunes] 启发式选择 index={best} name={options[best].Name} score={bestScore}");
        return best;
    }

    private static int ScoreOption(Player aiPlayer, RuneOptionInfo option)
    {
        int score = 0;
        var name = (option.Name ?? "").ToLowerInvariant();
        var id = (option.RelicId ?? "").ToLowerInvariant();
        var desc = (option.Description ?? "").ToLowerInvariant();

        var charName = (aiPlayer.Character?.Id.Entry ?? "").ToLowerInvariant();
        if (!string.IsNullOrEmpty(charName) && (name.Contains(charName) || id.Contains(charName)))
            score += 10;

        if (desc.Contains("rare") || desc.Contains("gold") || desc.Contains("legendary")
            || desc.Contains("稀有") || desc.Contains("棱彩") || desc.Contains("黄金"))
            score += 5;

        return score;
    }

    // ---- Payload ----

    private static PlayerChoiceResult? BuildRuneSelection(
        int actIndex, int choiceOrdinal, int selectedIndex, IReadOnlyList<RelicModel> options)
    {
        var create = _createRuneSelection;
        if (create == null) return null;

        try
        {
            return create.Invoke(null, new object?[]
            {
                actIndex,
                choiceOrdinal,
                selectedIndex,
                Array.Empty<int>(),
                options,
                null
            }) as PlayerChoiceResult;
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[HextechRunes] CreateRuneSelection 调用失败: {Unwrap(ex).Message}");
            return null;
        }
    }

    private static bool IsGodotAlive(object instance)
    {
        return instance is not Godot.GodotObject godotObject || Godot.GodotObject.IsInstanceValid(godotObject);
    }

    private static bool TryBeginAnswer(Player aiPlayer, uint choiceId)
    {
        lock (Gate)
        {
            var runState = RunManager.Instance?.DebugOnlyGetState();
            if (!ReferenceEquals(runState, _answerRun))
            {
                _answerRun = runState;
                AnsweredChoices.Clear();
            }

            return AnsweredChoices.Add((aiPlayer.NetId, choiceId));
        }
    }

    // ---- Solo screen ----

    /// <summary>
    /// Auto-answers a Hextech selection screen while the AI drives the local player (solo climb).
    /// The wait for a human click would otherwise soft-lock the run; a ▶/⏸ pause hands control back.
    /// </summary>
    public static void AutoAnswerSoloScreen(object screen)
    {
        if (screen == null) return;
        lock (Gate)
        {
            if (!AutoAnsweredScreens.Add(screen)) return;
        }

        _ = AutoAnswerSoloScreenAsync(screen);
    }

    private static async Task AutoAnswerSoloScreenAsync(object screen)
    {
        try
        {
            // Let the overlay finish building and entering the stack.
            await Task.Delay(400);

            if (!SoloRunController.IsActive || !IsGodotAlive(screen))
                return;

            await WaitWhilePausedAsync();

            bool enemyOnly = false;
            IReadOnlyList<RelicModel>? relics = null;
            try
            {
                (enemyOnly, relics) = MainThread.Invoke(() =>
                    (Read<bool>(screen, "PresenterEnemyOnly"),
                     Read<IReadOnlyList<RelicModel>>(screen, "CurrentRelics")));
            }
            catch (Exception ex)
            {
                MainFile.Logger.Warn($"[HextechRunes] 读取符文界面候选失败: {ex.Message}");
            }

            if (enemyOnly || relics == null || relics.Count == 0)
            {
                MainThread.Post(() =>
                {
                    if (SoloRunController.IsActive && !AiFlowControl.IsPaused
                        && IsGodotAlive(screen)
                        && !Read<bool>(screen, "PresenterLocked"))
                    {
                        Invoke(screen, "PresenterConfirmEnemyOnly");
                        MainFile.Logger.Info("[HextechRunes] 单人爬塔：自动确认海克斯界面");
                    }
                });
                return;
            }

            var aiPlayer = AiTeammateManager.Instance.AiPlayer;
            if (aiPlayer == null || !AiTeammateManager.Instance.IsInitialized)
                return;

            var infos = ExtractOptions(relics);
            int index = await DecideWithDeadlineAsync(aiPlayer, infos, -1, -1, null);
            if (index < 0 || index >= relics.Count)
                index = Math.Clamp(index, 0, relics.Count - 1);

            int finalIndex = index;
            MainThread.Post(() =>
            {
                if (!SoloRunController.IsActive || AiFlowControl.IsPaused)
                {
                    MainFile.Logger.Info("[HextechRunes] 单人爬塔已暂停/退出，保留海克斯选择界面给玩家");
                    return;
                }

                if (!IsGodotAlive(screen) || Read<bool>(screen, "PresenterLocked"))
                    return;

                Invoke(screen, "PresenterChoose", finalIndex);
                var name = PromptTextHelper.ResolveLocString(relics[finalIndex].Title);
                AiTeammateManager.Instance.AiLog.Log("AI_ACTION", $"[海克斯符文] 单人爬塔自动选择 {finalIndex}: {name}");
                MainFile.Logger.Info($"[HextechRunes] 单人爬塔自动选择符文 index={finalIndex} name={name}");
            });
        }
        catch (OperationCanceledException)
        {
            MainFile.Logger.Info("[HextechRunes] 玩家暂停了 AI，海克斯选择界面交还给玩家");
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[HextechRunes] 自动处理海克斯界面失败: {ex.Message}");
        }
        finally
        {
            lock (Gate)
            {
                AutoAnsweredScreens.Remove(screen);
            }
        }
    }

    private static async Task WaitWhilePausedAsync()
    {
        for (int i = 0; i < 60 && AiFlowControl.IsPaused; i++)
        {
            if (!SoloRunController.IsActive) return;
            await Task.Delay(500);
        }

        // Paused by the player: leave the screen for them to click.
        if (AiFlowControl.IsPaused) throw new OperationCanceledException();
    }

    // ---- Reflection helpers ----

    private static void Invoke(object instance, string methodName, params object?[] args)
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var methods = instance.GetType().GetMethods(Flags)
            .Where(m => m.Name == methodName && m.GetParameters().Length == args.Length)
            .ToList();
        var method = methods.FirstOrDefault(m => ParametersMatch(m.GetParameters(), args))
                     ?? methods.FirstOrDefault();
        if (method == null)
        {
            MainFile.Logger.Warn($"[HextechRunes] 找不到方法 {instance.GetType().Name}.{methodName}");
            return;
        }

        method.Invoke(instance, args);
    }

    private static bool ParametersMatch(ParameterInfo[] parameters, object?[] args)
    {
        for (int i = 0; i < parameters.Length; i++)
        {
            var value = args[i];
            if (value == null)
            {
                if (parameters[i].ParameterType.IsValueType) return false;
                continue;
            }

            if (!parameters[i].ParameterType.IsInstanceOfType(value)) return false;
        }

        return true;
    }

    private static T Read<T>(object instance, string memberName)
    {
        var type = instance.GetType();
        var property = type.GetProperty(memberName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property != null && property.GetValue(instance) is T value)
            return value;

        var field = type.GetField(memberName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field != null && field.GetValue(instance) is T fieldValue)
            return fieldValue;

        return default!;
    }

    private static string CleanDescription(string desc)
    {
        if (string.IsNullOrEmpty(desc)) return "";
        desc = Regex.Replace(desc, @"\[.*?\]", "");
        desc = Regex.Replace(desc, @"res://\S+", "");
        return desc.Trim();
    }

    private static Exception Unwrap(Exception exception)
    {
        while (exception is TargetInvocationException { InnerException: { } inner })
            exception = inner;
        return exception;
    }
}

public class RuneOptionInfo
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string RelicId { get; set; } = "";
}

public class MonsterHexInfo
{
    public string KindName { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public bool IsRemoved { get; set; }
}
