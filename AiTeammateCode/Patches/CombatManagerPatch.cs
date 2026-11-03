using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using AiTeammate.AiTeammateCode.Decision;
using AiTeammate.AiTeammateCode.Solo;

namespace AiTeammate.AiTeammateCode.Patches;

[HarmonyPatch(typeof(CombatManager))]
public static class CombatManagerPatch
{
    private static bool _eventsSubscribed;

    [HarmonyPatch(nameof(CombatManager.SetUpCombat))]
    [HarmonyPostfix]
    public static void AfterSetUpCombat(CombatState state)
    {
        var manager = AiTeammateManager.Instance;
        if (!manager.IsInitialized) return;

        // 新战斗开始，重置多轮对话历史
        manager.DecisionService.ResetCombatHistory();
        manager.DirectiveManager.ResetCombat();
        manager.ResetExtraPlayerTurnCombatDecisionState();
        CombatDecisionExecutor.ResetReadyToBeginEnemyTurnSignal();

        // Subscribe to combat events once per session
        if (!_eventsSubscribed)
        {
            CombatManager.Instance.TurnStarted += OnTurnStarted;
            CombatManager.Instance.CombatWon += OnCombatWon;
            _eventsSubscribed = true;
        }

        // Solo climb: bind (or release) the local player before combat starts.
        SoloRunController.TryBind(manager);

        // Bind from the live combat state. Must run on every combat, not only when nothing is bound:
        // a save load rebuilds every Player instance and the cached reference would stay stale.
        manager.DetectAndBindAiPlayer(state);
    }

    [HarmonyPatch(nameof(CombatManager.SetReadyToEndTurn))]
    [HarmonyPrefix]
    public static void BeforeSetReadyToEndTurn(Player player)
    {
        var manager = AiTeammateManager.Instance;
        if (!manager.IsInitialized || manager.AiPlayer == null) return;

        if (player == manager.AiPlayer) return;

        if (CombatManager.Instance.IsOverOrEnding) return;

        var state = CombatManager.Instance.DebugOnlyGetState();
        if (state?.CurrentSide != CombatSide.Player) return;

        if (!CombatManager.Instance.IsPlayerReadyToEndTurn(manager.AiPlayer)) return;
        if (!manager.HasExtraPlayerTurnCombatDecisionOpportunity()) return;
        if (!manager.TryPrepareExtraPlayerTurnCombatDecision()) return;

        try
        {
            CombatManager.Instance.UndoReadyToEndTurn(manager.AiPlayer);
            manager.AiLog.Log("COMBAT", "玩家结束回合时检测到AI仍有能量和可打牌，撤销ready并补发一次战斗决策");
        }
        catch (Exception ex)
        {
            manager.CancelPendingExtraPlayerTurnCombatDecision();
            MainFile.Logger.Warn($"Failed to prepare extra AI combat decision: {ex.Message}");
        }
    }

    [HarmonyPatch(nameof(CombatManager.SetReadyToEndTurn))]
    [HarmonyPostfix]
    public static void AfterSetReadyToEndTurn(Player player)
    {
        var manager = AiTeammateManager.Instance;
        if (!manager.IsInitialized || manager.AiPlayer == null) return;

        // Don't signal enemy turns if combat is already over
        if (CombatManager.Instance.IsOverOrEnding) return;

        var state = CombatManager.Instance.DebugOnlyGetState();
        if (state?.CurrentSide != CombatSide.Player) return;

        if (player == manager.AiPlayer) return;

        if (manager.ConsumePendingExtraPlayerTurnCombatDecision())
        {
            TriggerExtraCombatDecision(manager);
        }

        // The AI's own ready-to-begin-enemy-turn signal is sent by
        // CombatTurnTransitionPatch after end-of-turn phase one, never here: signalling straight
        // from a SetReadyToEndTurn postfix can run AfterAllPlayersReadyToBeginEnemyTurn while
        // phase one is still in flight (or, in singleplayer, duplicate the game's own transition).
    }

    private static async void OnTurnStarted(CombatState state)
    {
        var manager = AiTeammateManager.Instance;
        if (!manager.IsInitialized || manager.AiPlayer == null) return;

        // Don't process turns if combat is already over or ending.
        // TurnStarted can fire during reward processing when combat
        // state hasn't been fully cleaned up yet.
        if (CombatManager.Instance.IsOverOrEnding) return;

        if (state.CurrentSide == CombatSide.Player)
        {
            manager.ResetExtraPlayerTurnCombatDecisionState();
            CombatDecisionExecutor.ResetReadyToBeginEnemyTurnSignal();
        }

        try
        {
            await manager.ProcessCombatTurnAsync();
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"AI turn error: {ex.Message}");
        }
    }

    private static void OnCombatWon(MegaCrit.Sts2.Core.Rooms.CombatRoom room)
    {
        var manager = AiTeammateManager.Instance;
        if (!manager.IsInitialized || manager.AiPlayer == null)
            return;

        // A snapshot that arrived during the fight could not be applied safely; adopt it now.
        Compat.AiStateSync.ApplyPendingIfSafe("combat-won");

        _ = manager.OnCombatWon();
    }

    private static async void TriggerExtraCombatDecision(AiTeammateManager manager)
    {
        try
        {
            await manager.ProcessCombatTurnAsync();
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"Extra AI combat decision error: {ex.Message}");
        }
    }
}

/// <summary>
/// Signals the AI player's <c>ReadyToBeginEnemyTurnAction</c> at the right moment: when the game
/// enqueues the local player's own ready action, i.e. after end-of-turn phase one finished. The AI
/// has no network peer, so nothing else ever sends its action and the transition would never start.
/// Hooking <c>RequestEnqueue</c> (identical signature on 0.107.x and 0.111.x) is deliberate — a
/// postfix on the async <c>AfterAllPlayersReadyToEndTurn</c> would run when that method first yields,
/// before phase one is done, which is exactly the race that corrupts the turn.
/// </summary>
[HarmonyPatch(typeof(ActionQueueSynchronizer), nameof(ActionQueueSynchronizer.RequestEnqueue))]
public static class CombatTurnTransitionPatch
{
    [HarmonyPrefix]
    public static void BeforeRequestEnqueue(GameAction action)
    {
        if (action is not ReadyToBeginEnemyTurnAction)
            return;

        var manager = AiTeammateManager.Instance;
        if (!manager.IsInitialized)
            return;

        var aiPlayer = manager.AiPlayer;
        if (aiPlayer == null)
            return;

        CombatDecisionExecutor.TrySignalReadyToBeginEnemyTurn(aiPlayer);
    }
}
