using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Runs;
using AiTeammate.AiTeammateCode.Agent;
using AiTeammate.AiTeammateCode.Solo;

namespace AiTeammate.AiTeammateCode.Decision;

public class CombatDecisionExecutor
{
    private readonly AiLogger _log;
    private const int MaxCardsPerTurn = 20;
    private const int CoordinationPollMs = 120;
    private const int CoordinationShortWaitMs = 900;
    private const int CoordinationLongWaitMs = 2000;

    public CombatDecisionExecutor(AiLogger? log = null)
    {
        _log = log ?? AiTeammateManager.Instance.AiLog;
    }

    /// <summary>
    /// True when solo mode stopped this turn mid-way because the human interrupted the AI.
    /// The turn is intentionally left open (not ended) so it can continue after the chat.
    /// </summary>
    public bool PausedMidTurn { get; private set; }

    private static bool SoloPaused =>
        AiTeammateManager.Instance.IsSoloControlled && AiFlowControl.IsPaused;

    private static bool _signaledReadyToBeginEnemyTurn;

    /// <summary>
    /// Clears the per-transition signal guard. Called by the combat patch whenever a player turn
    /// starts, because <c>CombatManager</c> clears its own ready-to-begin set at that point.
    /// </summary>
    public static void ResetReadyToBeginEnemyTurnSignal() => _signaledReadyToBeginEnemyTurn = false;

    /// <summary>
    /// Tells <c>CombatManager</c> that the AI player is ready to switch to the enemy side.
    /// The AI has no network peer, so nothing else ever sends its <c>ReadyToBeginEnemyTurnAction</c>.
    /// Called once per player→enemy transition by <c>CombatTurnTransitionPatch</c>, which hooks the
    /// game enqueueing the local player's ready action (i.e. after end-of-turn phase one).
    /// </summary>
    public static bool TrySignalReadyToBeginEnemyTurn(Player aiPlayer)
    {
        var manager = AiTeammateManager.Instance;
        if (!manager.IsInitialized || manager.AiPlayer == null)
            return false;

        var combatManager = CombatManager.Instance;
        if (!combatManager.IsInProgress || combatManager.IsOverOrEnding)
            return false;

        // 0.107.x: SetReadyToBeginEnemyTurn starts the whole player→enemy transition unconditionally
        // in singleplayer (`allPlayersReady || netType == Singleplayer`) with no "already claimed"
        // guard, and the game itself enqueues the local player's ReadyToBeginEnemyTurnAction. The
        // local player is the only player there (and in solo climb it *is* the AI), so an extra call
        // here starts a second concurrent AfterAllPlayersReadyToBeginEnemyTurn — which is what makes
        // the game throw "EndPlayerTurnPhaseTwo called while the current side is Enemy!" and
        // "EndEnemyTurn called while the current side is Player!". Let the game drive it.
        var netType = RunManager.Instance?.NetService?.Type;
        if (netType is null or NetGameType.None or NetGameType.Singleplayer or NetGameType.Replay)
            return false;

        var state = combatManager.DebugOnlyGetState();
        if (state?.CurrentSide != CombatSide.Player)
            return false;
        if (!state.Players.Contains(aiPlayer))
            return false;

        // Repeated calls while the ready set is already full would claim the transition again.
        if (_signaledReadyToBeginEnemyTurn)
            return false;

        _signaledReadyToBeginEnemyTurn = true;
        combatManager.SetReadyToBeginEnemyTurn(aiPlayer);
        return true;
    }

    public async Task<int> Execute(List<DecisionAction> actions, List<DecisionAction> deferredActions, Player aiPlayer, CancellationToken ct = default, bool endTurn = true)
    {
        PausedMidTurn = false;
        var state = CombatManager.Instance.DebugOnlyGetState();
        var combat = aiPlayer.PlayerCombatState;
        if (state == null || combat == null) return 0;

        var starsInfo = combat.Stars > 0 ? $" 辉星:{combat.Stars}" : "";
        _log.Log("COMBAT", $"回合开始 — 手牌:{combat.Hand.Cards.Count}张 能量:{combat.Energy}/{combat.MaxEnergy}{starsInfo}");

        int initialEnergy = combat.Energy;
        int cardsPlayed = 0;

        try
        {
            // Phase 0: Use potions first (free actions, don't consume energy)
            var potionActions = actions
                .Where(a => a.Type == "use_potion" && !string.IsNullOrWhiteSpace(a.PotionName))
                .ToList();
            foreach (var potionAction in potionActions)
            {
                if (SoloPaused) { PausedMidTurn = true; break; }
                if (CombatManager.Instance.IsOverOrEnding) break;
                if (await UseOnePotion(aiPlayer, state, potionAction, ct))
                    AiTeammateManager.Instance.DirectiveManager.RegisterExecutedAction(aiPlayer, potionAction);
            }

            if (PausedMidTurn)
            {
                _log.Log("COMBAT", "玩家打断：停止出牌（保留当前回合）");
                return cardsPlayed;
            }

            var immediateActions = actions
                .Where(a => a.Type == "play_card" && !string.IsNullOrWhiteSpace(a.CardName))
                .ToList();

            if (AgentDecisionBroker.Enabled)
            {
                // The external agent already reasoned about sequencing (apply Vulnerable first, draw
                // before the payoff card, ...). Splitting the list into "all non-attacks, then all
                // attacks" silently reordered its plan; play the actions in the order it returned.
                // 外部大脑给出的顺序有战术含义，按原序执行。
                cardsPlayed += await PlayPlannedActions(aiPlayer, immediateActions, "外部大脑出牌", deferredPhase: false, ct);
                if (PausedMidTurn) return cardsPlayed;

                if (deferredActions.Count > 0 && !CombatManager.Instance.IsOverOrEnding)
                {
                    _log.Log("COMBAT", $"协同延迟出牌: {deferredActions.Count}张 — 等队友先行动");
                    await WaitForCoordinationWindow(aiPlayer, deferredActions, deferredActions.Count > 0, ct);
                    cardsPlayed += await PlayPlannedActions(aiPlayer, deferredActions, "协同延迟出牌", deferredPhase: true, ct);
                    if (PausedMidTurn) return cardsPlayed;
                }
            }
            else
            {
                var setupActions = new List<DecisionAction>();
                var attackActions = new List<DecisionAction>();
                foreach (var action in immediateActions)
                {
                    var card = FindCardInHand(combat, action.CardName);
                    if (card == null)
                    {
                        _log.Log("AI_ACTION", $"跳过 [{action.CardName}]: 不在手牌中");
                        continue;
                    }

                    if (card.Type == CardType.Attack)
                        attackActions.Add(action);
                    else
                        setupActions.Add(action);
                }

                cardsPlayed += await PlayPlannedActions(aiPlayer, setupActions, "第1阶段出牌", deferredPhase: false, ct);
                if (PausedMidTurn) return cardsPlayed;

                var coordinatedAttackQueue = new List<DecisionAction>();
                coordinatedAttackQueue.AddRange(deferredActions);
                coordinatedAttackQueue.AddRange(attackActions);

                if (coordinatedAttackQueue.Count > 0 && !CombatManager.Instance.IsOverOrEnding)
                {
                    if (deferredActions.Count > 0)
                        _log.Log("COMBAT", $"协同延迟出牌: {deferredActions.Count}张 — 等队友先行动");

                    await WaitForCoordinationWindow(aiPlayer, coordinatedAttackQueue, deferredActions.Count > 0, ct);
                    cardsPlayed += await PlayPlannedActions(aiPlayer, coordinatedAttackQueue, "第2阶段出牌", deferredPhase: true, ct);
                    if (PausedMidTurn) return cardsPlayed;
                }
            }

            // Phase 3: Play any remaining playable cards.
            // Not when an external agent supplies the decisions: cards drawn mid-turn (by cards or
            // potions) must be seen by it, so the pass loop re-asks instead of spending the energy
            // on cards it never saw. The caller decides whether another decision pass is worth it.
            if (!AgentDecisionBroker.Enabled
                && !CombatManager.Instance.IsOverOrEnding
                && (combat.Energy > 0 || combat.Stars > 0))
            {
                cardsPlayed += await PlayRemainingCards(aiPlayer, state, combat, cardsPlayed);
            }
        }
        catch (OperationCanceledException)
        {
            _log.Log("COMBAT", "回合执行被取消");
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError("COMBAT", $"回合执行异常: {ex.Message}");
            // Fall through to finally so turn still ends
        }
        finally
        {
            // Phase 4: End turn — always attempt this, even if exceptions occurred.
            // If we don't end the turn, the game deadlocks waiting for the AI player.
            // Use IsInProgress instead of !IsOverOrEnding: when AI kills the last enemy,
            // IsEnding becomes true (no alive primary enemies) but IsInProgress remains true
            // until EndCombatInternal runs. We must still signal SetReadyToEndTurn so the
            // game can transition through AllPlayersReadyToEndTurn → CheckWinCondition → EndCombatInternal.
            var currentState = CombatManager.Instance.DebugOnlyGetState();
            if (endTurn
                && !PausedMidTurn
                && CombatManager.Instance.IsInProgress
                && currentState?.CurrentSide == CombatSide.Player)
            {
                try
                {
                    _log.Log("AI_ACTION", $"结束回合 | 出牌{cardsPlayed}张 能量 {initialEnergy}→{combat.Energy}");
                    // The player→enemy transition is signalled once, after end-of-turn phase one
                    // (see TrySignalReadyToBeginEnemyTurn); signalling here would race the queued
                    // ReadyToBeginEnemyTurnAction.
                    CombatManager.Instance.SetReadyToEndTurn(aiPlayer, canBackOut: false);
                }
                catch (Exception endEx)
                {
                    _log.LogError("COMBAT", $"结束回合失败: {endEx.Message}");
                }
            }
            else if (currentState?.CurrentSide != CombatSide.Player)
            {
                _log.Log("AI_ACTION", $"跳过结束回合 — 当前已是敌方回合");
            }

            _log.Flush();
        }

        return cardsPlayed;
    }

    public void TryEndTurn(Player aiPlayer)
    {
        var combat = aiPlayer.PlayerCombatState;
        var currentState = CombatManager.Instance.DebugOnlyGetState();
        if (combat == null)
            return;

        if (CombatManager.Instance.IsInProgress
            && currentState?.CurrentSide == CombatSide.Player)
        {
            try
            {
                _log.Log("AI_ACTION", $"结束回合 | 当前能量 {combat.Energy} 辉星 {combat.Stars}");
                // Transition signal is handled after end-of-turn phase one (TrySignalReadyToBeginEnemyTurn).
                CombatManager.Instance.SetReadyToEndTurn(aiPlayer, canBackOut: false);
            }
            catch (Exception endEx)
            {
                _log.LogError("COMBAT", $"结束回合失败: {endEx.Message}");
            }
        }
        else if (currentState?.CurrentSide != CombatSide.Player)
        {
            _log.Log("AI_ACTION", "跳过结束回合 — 当前已是敌方回合");
        }

        _log.Flush();
    }

    private async Task<int> PlayPlannedActions(
        Player aiPlayer,
        List<DecisionAction> actions,
        string phaseLabel,
        bool deferredPhase,
        CancellationToken ct)
    {
        var state = CombatManager.Instance.DebugOnlyGetState();
        var combat = aiPlayer.PlayerCombatState;
        if (state == null || combat == null)
            return 0;

        var played = 0;
        foreach (var action in actions)
        {
            ct.ThrowIfCancellationRequested();

            if (SoloPaused)
            {
                PausedMidTurn = true;
                break;
            }

            if (CombatManager.Instance.IsOverOrEnding) break;
            if (played >= MaxCardsPerTurn) break;
            if (combat.Energy <= 0 && combat.Stars <= 0) break;

            var card = FindCardInHand(combat, action.CardName);
            if (card == null)
            {
                _log.Log("AI_ACTION", $"{(deferredPhase ? "延迟跳过" : "跳过")} [{action.CardName}]: 不在手牌中");
                continue;
            }

            if (!combat.HasEnoughResourcesFor(card, out var reason))
            {
                _log.Log("AI_ACTION", $"{(deferredPhase ? "延迟跳过" : "跳过")} [{action.CardName}]: 资源不足 ({reason})");
                continue;
            }

            var target = FindTarget(state, aiPlayer, action.Target);
            if (deferredPhase)
            {
                var waitReason = string.IsNullOrWhiteSpace(action.WaitFor) ? "等待队友先行动" : action.WaitFor;
                _log.Log("AI_ACTION", $"协同出牌 [{card.Title ?? card.Id.Entry}] — 等待原因: {waitReason}");
            }

            await PlayOneCard(aiPlayer, state, card, target);
            AiTeammateManager.Instance.DirectiveManager.RegisterExecutedAction(aiPlayer, action);
            played++;
            _log.Log("AI_ACTION", $"{phaseLabel}: [{card.Title ?? card.Id.Entry}]");
        }

        return played;
    }

    private async Task PlayOneCard(Player aiPlayer, CombatState state, CardModel card, Creature? target)
    {
        var cost = PromptTextHelper.ResolveEnergyCost(card);
        var starCost = Math.Max(0, card.GetStarCostWithModifiers());
        var preEnergy = aiPlayer.PlayerCombatState?.Energy ?? 0;
        var preStars = aiPlayer.PlayerCombatState?.Stars ?? 0;
        _log.LogCardPlay(card.Title ?? card.Id.Entry, target?.Name ?? target?.Monster?.Id.Entry);

        // HookPlayerChoiceContext only enqueues/resumes choice hooks when localPlayerId
        // matches the hook action owner. For AI-controlled cards that need a follow-up
        // selection (Burning Pact, True Grit, Survivor, etc.), passing the human local
        // player id causes SignalPlayerChoiceBegun() to wait forever for a hook action
        // that is never enqueued. Use the AI player's NetId here so the local host can
        // drive those follow-up choices on the AI player's queue.
        var hookPlayerId = aiPlayer.NetId;
        var choiceContext = new HookPlayerChoiceContext(
            aiPlayer,
            hookPlayerId,
            GameActionType.CombatPlayPhaseOnly);

        var playTask = CardCmd.AutoPlay(choiceContext, card, target, AutoPlayType.Default);
        await choiceContext.AssignTaskAndWaitForPauseOrCompletion(playTask);
        await playTask;

        var postEnergy = aiPlayer.PlayerCombatState?.Energy ?? 0;
        var postStars = aiPlayer.PlayerCombatState?.Stars ?? 0;
        var starInfo = starCost > 0 ? $" 辉星耗:{starCost} 辉星 {preStars}→{postStars}" : "";
        _log.Log("AI_PLAY", $"打出 [{card.Title ?? card.Id.Entry}] | 能耗:{cost} | 能量 {preEnergy}→{postEnergy}{starInfo}");

        if (postEnergy == preEnergy && cost > 0 && aiPlayer.PlayerCombatState != null)
        {
            aiPlayer.PlayerCombatState.LoseEnergy(cost);
            _log.Log("AI_DEBUG", $"强制扣除能量: {preEnergy}→{aiPlayer.PlayerCombatState.Energy}");
        }

        if (postStars == preStars && starCost > 0 && aiPlayer.PlayerCombatState != null)
        {
            aiPlayer.PlayerCombatState.LoseStars(starCost);
            _log.Log("AI_DEBUG", $"强制扣除辉星: {preStars}→{aiPlayer.PlayerCombatState.Stars}");
        }
    }

    private async Task<bool> UseOnePotion(Player aiPlayer, CombatState state, DecisionAction action, CancellationToken ct)
    {
        var potions = aiPlayer.Potions;
        if (potions == null || !potions.Any()) return false;

        var potion = potions.FirstOrDefault(p =>
        {
            var title = PromptTextHelper.ResolveLocString(p.Title, p.Id.Entry);
            return title.Contains(action.PotionName, System.StringComparison.OrdinalIgnoreCase)
                || p.Id.Entry.Contains(action.PotionName, System.StringComparison.OrdinalIgnoreCase);
        });

        if (potion == null)
        {
            _log.Log("AI_ACTION", $"跳过药水 [{action.PotionName}]: 未找到");
            return false;
        }

        // Find target if specified
        Creature? target = null;
        if (!string.IsNullOrWhiteSpace(action.Target))
        {
            target = FindTarget(state, aiPlayer, action.Target);
        }

        // If potion targets Self and no target specified, target the AI player
        if (target == null && potion.TargetType == TargetType.Self)
        {
            target = aiPlayer.Creature;
        }

        _log.Log("AI_ACTION", $"使用药水 [{PromptTextHelper.ResolveLocString(potion.Title, action.PotionName)}]{(target != null ? $" -> {target.Name}" : "")}");

        // Multiplayer: the host owns the AI's potion action. A follower must not enqueue a second
        // copy (that used to consume a potion twice, or even the human's potion before the action
        // owner fix); it just waits for the host's action to replicate and consume the slot.
        if (Compat.AiMultiplayer.IsFollower)
        {
            var before = aiPlayer.Potions?.Count() ?? 0;
            _log.Log("AI_ACTION", "跟随房主使用药水，等待同步");
            for (int i = 0; i < 30; i++)
            {
                await Task.Delay(100, ct);
                var now = aiPlayer.Potions?.Count() ?? 0;
                if (now < before)
                    break;
            }
            return true;
        }

        try
        {
            var usePotionAction = new UsePotionAction(potion, target, CombatManager.Instance.IsInProgress);
            RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(usePotionAction);
            _log.Log("AI_ACTION", $"药水已入队: {PromptTextHelper.ResolveLocString(potion.Title, action.PotionName)}");
            await usePotionAction.CompletionTask.WaitAsync(ct);
            _log.Log("AI_ACTION", $"药水执行完成: {PromptTextHelper.ResolveLocString(potion.Title, action.PotionName)}");
            return true;
        }
        catch (OperationCanceledException)
        {
            _log.Log("AI_ACTION", $"药水执行被取消: {PromptTextHelper.ResolveLocString(potion.Title, action.PotionName)}");
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError("AI_ACTION", $"药水使用失败 [{PromptTextHelper.ResolveLocString(potion.Title, action.PotionName)}]: {ex.Message}");
            return false;
        }
    }

    private async Task<int> PlayRemainingCards(Player aiPlayer, CombatState state, PlayerCombatState combat, int alreadyPlayed)
    {
        int extraPlayed = 0;
        int maxExtra = MaxCardsPerTurn - alreadyPlayed;
        var preferredTargetName = AiTeammateManager.Instance.DirectiveManager.GetPreferredFocusTarget();
        bool enemyAttacking = state.Enemies.Any(e =>
            e.IsAlive && e.Monster?.NextMove?.Intents.Any(i =>
                i.IntentType == IntentType.Attack) == true);

        for (int i = 0; i < maxExtra; i++)
        {
            if (SoloPaused) { PausedMidTurn = true; break; }
            if (CombatManager.Instance.IsOverOrEnding) break;
            if (combat.Energy <= 0 && combat.Stars <= 0) break;

            var hand = combat.Hand.Cards.ToList();
            if (hand.Count == 0) break;

            var playable = hand
                .Where(c => !c.Keywords.Contains(CardKeyword.Unplayable)
                         && combat.HasEnoughResourcesFor(c, out _))
                .ToList();

            if (playable.Count == 0) break;

            CardModel? bestCard = null;
            Creature? bestTarget = null;

            var attacks = playable.Where(c => c.Type == CardType.Attack).OrderByDescending(c => PromptTextHelper.ResolveEnergyCost(c)).ToList();
            if (attacks.Count > 0)
            {
                bestCard = attacks[0];
                bestTarget = !string.IsNullOrWhiteSpace(preferredTargetName)
                    ? state.Enemies.FirstOrDefault(e =>
                        e.IsAlive
                        && (MatchesTargetAlias(e.Name, preferredTargetName) || MatchesTargetAlias(e.Monster?.Id.Entry, preferredTargetName)))
                    : null;
                bestTarget ??= state.Enemies.Where(e => e.IsAlive).OrderBy(e => e.CurrentHp).FirstOrDefault();
            }
            else if (enemyAttacking)
            {
                var skills = playable.Where(c => c.Type == CardType.Skill).OrderByDescending(c => PromptTextHelper.ResolveEnergyCost(c)).ToList();
                if (skills.Count > 0) bestCard = skills[0];
            }
            else
            {
                var powers = playable.Where(c => c.Type == CardType.Power).OrderByDescending(c => PromptTextHelper.ResolveEnergyCost(c)).ToList();
                if (powers.Count > 0) bestCard = powers[0];
                else bestCard = playable.OrderByDescending(c => PromptTextHelper.ResolveEnergyCost(c)).FirstOrDefault();
            }

            if (bestCard == null) break;

            _log.Log("AI_ACTION", $"额外出牌: [{bestCard.Title ?? bestCard.Id.Entry}]");
            await PlayOneCard(aiPlayer, state, bestCard, bestTarget);
            extraPlayed++;
        }

        return extraPlayed;
    }

    private async Task WaitForCoordinationWindow(Player aiPlayer, List<DecisionAction> coordinatedActions, bool hasExplicitDeferred, CancellationToken ct)
    {
        var state = CombatManager.Instance.DebugOnlyGetState();
        if (state == null || coordinatedActions.Count == 0)
            return;

        var teammates = state.Players
            .Where(p => p != aiPlayer && p.Creature.IsAlive && p.PlayerCombatState != null)
            .ToList();
        if (teammates.Count == 0)
            return;

        var activeTeammates = teammates
            .Where(p => !CombatManager.Instance.IsPlayerReadyToEndTurn(p)
                && (p.PlayerCombatState!.Energy > 0 || p.PlayerCombatState.Stars > 0)
                && p.PlayerCombatState.Hand.Cards.Count > 0)
            .ToList();
        if (activeTeammates.Count == 0)
            return;

        var maxWaitMs = hasExplicitDeferred || HasStrongCoordinationSignal(activeTeammates, state)
            ? CoordinationLongWaitMs
            : CoordinationShortWaitMs;
        var baseline = CaptureCoordinationSnapshot(state, aiPlayer);

        _log.Log("COMBAT", $"等待队友协同... ({coordinatedActions.Count}张候选攻击, 最多{maxWaitMs}ms)");
        for (var waited = 0; waited < maxWaitMs; waited += CoordinationPollMs)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(CoordinationPollMs, ct);

            state = CombatManager.Instance.DebugOnlyGetState();
            if (state == null || CombatManager.Instance.IsOverOrEnding || state.CurrentSide != CombatSide.Player)
                return;

            // Re-check teammate liveness: a teammate may have died during the wait.
            // If all original teammates are now dead or have ended turn, stop waiting.
            bool anyAliveTeammate = false;
            foreach (var p in state.Players)
            {
                if (p == aiPlayer) continue;
                if (!p.Creature.IsAlive) continue;
                if (p.PlayerCombatState == null) continue;
                if (!CombatManager.Instance.IsPlayerReadyToEndTurn(p)
                    && (p.PlayerCombatState.Energy > 0 || p.PlayerCombatState.Stars > 0)
                    && p.PlayerCombatState.Hand.Cards.Count > 0)
                {
                    anyAliveTeammate = true;
                    break;
                }
            }
            if (!anyAliveTeammate)
            {
                _log.Log("COMBAT", "所有队友已无法行动（阵亡或已结束回合），跳过协同等待");
                return;
            }

            if (TryDescribeCoordinationProgress(baseline, CaptureCoordinationSnapshot(state, aiPlayer), out var reason))
            {
                _log.Log("COMBAT", $"检测到队友协同: {reason}");
                return;
            }

            var runningAction = RunManager.Instance?.ActionExecutor?.CurrentlyRunningAction;
            if (runningAction != null && runningAction.OwnerId != aiPlayer.NetId)
                continue;
        }

        _log.Log("COMBAT", "协同等待超时，按当前局势执行攻击牌");
    }

    private bool HasStrongCoordinationSignal(List<Player> teammates, CombatState state)
    {
        foreach (var teammate in teammates)
        {
            var combat = teammate.PlayerCombatState;
            if (combat == null) continue;

            foreach (var card in combat.Hand.Cards)
            {
                var desc = SafeCardDescription(card);
                if (string.IsNullOrWhiteSpace(desc))
                    continue;

                if (desc.Contains("易伤") || desc.Contains("破甲") || desc.Contains("虚弱")
                    || desc.Contains("Vulnerable") || desc.Contains("Weak"))
                    return true;
                if (desc.Contains("力量") || desc.Contains("敏捷") || desc.Contains("缓冲")
                    || desc.Contains("Strength") || desc.Contains("Dexterity") || desc.Contains("Buffer"))
                    return true;
                if (desc.Contains("所有敌人") || desc.Contains("每个敌人")
                    || desc.Contains("ALL enemies") || desc.Contains("all enemies"))
                    return true;

                if (card.Type == CardType.Attack)
                {
                    var damageMatch = System.Text.RegularExpressions.Regex.Match(desc, @"造成(\d+)点伤害");
                    if (!damageMatch.Success)
                        damageMatch = System.Text.RegularExpressions.Regex.Match(desc, @"[Dd]eal (\d+) damage");
                    if (damageMatch.Success && int.TryParse(damageMatch.Groups[1].Value, out var damage)
                        && state.Enemies.Any(e => e.IsAlive && e.CurrentHp <= damage + 2))
                        return true;
                }
            }
        }

        return false;
    }

    private CoordinationSnapshot CaptureCoordinationSnapshot(CombatState state, Player aiPlayer)
    {
        var snapshot = new CoordinationSnapshot();

        foreach (var player in state.Players.Where(p => p != aiPlayer))
        {
            var combat = player.PlayerCombatState;
            snapshot.PlayerStates[player.NetId] = new PlayerCoordinationState
            {
                Energy = combat?.Energy ?? 0,
                Stars = combat?.Stars ?? 0,
                HandCount = combat?.Hand.Cards.Count ?? 0,
                Hp = player.Creature.CurrentHp,
                Block = player.Creature.Block,
                ReadyToEndTurn = CombatManager.Instance.IsPlayerReadyToEndTurn(player),
                Powers = BuildPowerSignature(player.Creature.Powers)
            };
        }

        for (var i = 0; i < state.Enemies.Count; i++)
        {
            var enemy = state.Enemies[i];
            snapshot.EnemyStates[$"{i}:{enemy.Name ?? enemy.Monster?.Id.Entry ?? "Enemy"}"] = new EnemyCoordinationState
            {
                Hp = enemy.CurrentHp,
                Block = enemy.Block,
                Powers = BuildPowerSignature(enemy.Powers)
            };
        }

        return snapshot;
    }

    private bool TryDescribeCoordinationProgress(CoordinationSnapshot before, CoordinationSnapshot after, out string reason)
    {
        foreach (var (playerId, previous) in before.PlayerStates)
        {
            if (!after.PlayerStates.TryGetValue(playerId, out var current))
                continue;

            if (current.Energy < previous.Energy || current.Stars < previous.Stars)
            {
                reason = $"队友{playerId}已消耗资源行动";
                return true;
            }

            if (current.HandCount != previous.HandCount)
            {
                reason = $"队友{playerId}手牌变化";
                return true;
            }

            if (current.Powers != previous.Powers || current.Block != previous.Block)
            {
                reason = $"队友{playerId}状态已变化";
                return true;
            }

            if (current.ReadyToEndTurn && !previous.ReadyToEndTurn)
            {
                reason = $"队友{playerId}已结束操作";
                return true;
            }
        }

        foreach (var (enemyKey, previous) in before.EnemyStates)
        {
            if (!after.EnemyStates.TryGetValue(enemyKey, out var current))
                continue;

            if (current.Hp != previous.Hp || current.Block != previous.Block || current.Powers != previous.Powers)
            {
                reason = $"敌方目标 {enemyKey} 状态已变化";
                return true;
            }
        }

        reason = "";
        return false;
    }

    private static string BuildPowerSignature(IEnumerable<PowerModel>? powers)
    {
        if (powers == null)
            return "";

        return string.Join("|", powers
            .OrderBy(p => p.Id.Entry, StringComparer.Ordinal)
            .Select(p => $"{p.Id.Entry}:{p.Amount}"));
    }

    private static string SafeCardDescription(CardModel card)
    {
        try
        {
            return PromptTextHelper.GetCardDescriptionText(card, PileType.Hand);
        }
        catch
        {
            return "";
        }
    }

    private CardModel? FindCardInHand(PlayerCombatState combat, string cardName)
    {
        return combat.Hand.Cards.FirstOrDefault(c =>
            (c.Title ?? c.Id.Entry).Contains(cardName, System.StringComparison.OrdinalIgnoreCase)
            || c.Id.Entry.Contains(cardName, System.StringComparison.OrdinalIgnoreCase));
    }

    private Creature? FindTarget(CombatState state, Player aiPlayer, string targetName)
    {
        if (string.IsNullOrWhiteSpace(targetName)) return null;

        var normalized = NormalizeTargetText(targetName);
        if (normalized is "你" or "你自己" or "自己" or "ai" or "self" or "me" or "myself" or "you")
            return aiPlayer.Creature;

        var enemy = state.Enemies.FirstOrDefault(e =>
            MatchesTargetAlias(e.Name, targetName) || MatchesTargetAlias(e.Monster?.Id.Entry, targetName));
        if (enemy != null)
            return enemy;

        var player = state.Players.FirstOrDefault(p => MatchesPlayerAlias(p, aiPlayer, normalized, targetName));
        if (player != null)
            return player.Creature;

        if (normalized is "我" or "我自己" or "玩家" or "人类玩家" or "队友"
            or "player" or "human" or "teammate" or "ally")
            return state.Players.FirstOrDefault(p => p != aiPlayer && p.Creature.IsAlive)?.Creature;

        return null;
    }

    private static bool MatchesPlayerAlias(Player player, Player aiPlayer, string normalizedTarget, string rawTarget)
    {
        if (player == aiPlayer && normalizedTarget is not "你" and not "你自己" and not "自己"
            and not "ai" and not "self" and not "me" and not "myself" and not "you")
            return false;

        var aliases = new[]
        {
            GetPlayerDisplayName(player),
            player.Character?.Id.Entry ?? "",
            $"玩家{(int)(player.NetId & 0xFFFF)}"
        };

        return aliases.Any(alias => MatchesTargetAlias(alias, rawTarget));
    }

    private static bool MatchesTargetAlias(string? alias, string targetName)
    {
        if (string.IsNullOrWhiteSpace(alias) || string.IsNullOrWhiteSpace(targetName))
            return false;

        var left = NormalizeTargetText(alias);
        var right = NormalizeTargetText(targetName);
        return left.Contains(right, StringComparison.OrdinalIgnoreCase)
            || right.Contains(left, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeTargetText(string value)
    {
        var cleaned = PromptTextHelper.CleanText(value ?? "");
        cleaned = cleaned.Replace(" ", "", StringComparison.Ordinal)
            .Replace("\r", "", StringComparison.Ordinal)
            .Replace("\n", "", StringComparison.Ordinal)
            .Trim();
        return cleaned.ToLowerInvariant();
    }

    private static string GetPlayerDisplayName(Player player)
    {
        try
        {
            var platform = RunManager.Instance?.NetService?.Platform;
            if (platform.HasValue)
            {
                var name = PlatformUtil.GetPlayerName(platform.Value, player.NetId);
                if (!string.IsNullOrWhiteSpace(name))
                    return name;
            }
        }
        catch { }

        return player.Character?.Id.Entry ?? $"玩家{(int)(player.NetId & 0xFFFF)}";
    }

    private sealed class CoordinationSnapshot
    {
        public Dictionary<ulong, PlayerCoordinationState> PlayerStates { get; } = new();
        public Dictionary<string, EnemyCoordinationState> EnemyStates { get; } = new(StringComparer.Ordinal);
    }

    private sealed class PlayerCoordinationState
    {
        public int Energy { get; init; }
        public int Stars { get; init; }
        public int HandCount { get; init; }
        public int Hp { get; init; }
        public int Block { get; init; }
        public bool ReadyToEndTurn { get; init; }
        public string Powers { get; init; } = "";
    }

    private sealed class EnemyCoordinationState
    {
        public int Hp { get; init; }
        public int Block { get; init; }
        public string Powers { get; init; } = "";
    }
}
