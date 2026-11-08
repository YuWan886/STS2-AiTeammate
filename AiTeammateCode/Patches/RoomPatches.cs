using MegaCrit.Sts2.Core.AutoSlay.Helpers;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game.PeerInput;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using AiTeammate.AiTeammateCode.Decision;
using AiTeammate.AiTeammateCode.Compat;
using AiTeammate.AiTeammateCode.Solo;
using AiTeammate.AiTeammateCode.Agent;
using AiTeammate.AiTeammateCode.Personality;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiTeammate.AiTeammateCode.Patches;

// ---- Rewards: OfferForRoomEnd is only called for the local player ---
// Piggyback on the human's call to also trigger AI reward processing.
// ---- Helper: run a Task for AI without logging NRE as ERROR ----
// The AI player's game state (PlayerOdds, UnlockState, etc.) isn't fully
// initialized for server-side reward/rest operations.  NREs thrown inside
// these game methods are expected and harmless — the actual AI reward
// processing is handled by RewardsSetOfferPatch which intercepts Offer().
internal static class AiTaskRunner
{
    public static async void RunAiTaskSafely(Task task, string context)
    {
        try { await task; }
        catch (NullReferenceException)
        {
            // Expected: AI state not fully initialized for this game path
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[AI] {context} error: {ex.Message}");
        }
    }
}

[HarmonyPatch(typeof(RewardsCmd), nameof(RewardsCmd.OfferForRoomEnd))]
public static class OfferForRoomEndPatch
{
    [HarmonyPostfix]
    public static void AfterOfferForRoomEnd(Player player, AbstractRoom room)
    {
        var manager = AiTeammateManager.Instance;
        if (!manager.IsInitialized || manager.AiPlayer == null) return;
        if (player.NetId == manager.AiPlayer.NetId) return; // already handling AI

        var aiPlayer = manager.AiPlayer;

        // Verify AI was in this combat before triggering rewards
        var combat = CombatManager.Instance.DebugOnlyGetState();
        if (combat == null || !combat.Players.Any(p => p.NetId == aiPlayer.NetId))
            return;

        manager.AiLog.Log("AI_ACTION", "战斗结束，开始处理奖励");
        MainFile.Logger.Info("[REWARD] Triggering AI reward processing");

        try
        {
            // Run OfferForRoomEnd for AI. The inner WithRewardsFromRoom may throw
            // NRE because AI PlayerOdds / UnlockState etc. aren't fully initialized.
            // Actual reward processing is handled by RewardsSetOfferPatch which
            // intercepts Offer() for AI players regardless of the caller.
            AiTaskRunner.RunAiTaskSafely(RewardsCmd.OfferForRoomEnd(aiPlayer, room), "OfferForRoomEnd");
        }
        catch (Exception ex) { MainFile.Logger.Warn($"[REWARD] OfferForRoomEnd error: {ex.Message}"); }
    }
}

// Intercept Offer() for AI — it will return early at IsMe check otherwise
[HarmonyPatch(typeof(RewardsSet), nameof(RewardsSet.Offer))]
public static class RewardsSetOfferPatch
{
    [HarmonyPrefix]
    public static bool Prefix(RewardsSet __instance, ref Task __result)
    {
        var manager = AiTeammateManager.Instance;
        if (!manager.IsInitialized || manager.AiPlayer == null) return true;
        if (__instance.Player.NetId != manager.AiPlayer.NetId) return true;

        // Multiplayer followers do not claim the AI's rewards locally: the host owns the whole
        // reward flow (LLM choice included) and replicates the resulting AI state afterwards.
        // Running it on both peers with two different LLM answers is what desynced the run.
        if (!AiMultiplayer.IsAuthority)
        {
            manager.AiLog.Log("REWARD", "非房主，跳过 AI 奖励结算（由房主结算并同步状态）");
            __result = Task.CompletedTask;
            return false;
        }

        manager.AiLog.Log("REWARD", "拦截Offer()，跳过原始方法");
        __result = AutoClaimAiRewardsAsync(__instance, manager);
        return false;
    }

    private static async Task AutoClaimAiRewardsAsync(RewardsSet rewardsSet, AiTeammateManager manager)
    {
        var log = manager.AiLog;
        var aiPlayer = rewardsSet.Player;
        log.Log("REWARD", $"AI rewards Offer() intercepted for {aiPlayer.Character?.Id.Entry}");

        try
        {
            // Solo climb: if the human interrupted the AI, hold the reward flow too.
            if (SoloRunController.IsActive)
                await AiFlowControl.WaitWhilePausedAsync();

            // Step 1: Generate the rewards (cards, gold amounts, etc.)
            await rewardsSet.GenerateWithoutOffering();
            var rewards = rewardsSet.Rewards.ToList();
            log.Log("REWARD", $"Generated {rewards.Count} rewards");

            // Step 2: Auto-claim non-card rewards (gold, relic, potion)
            foreach (var reward in rewards.ToList())
            {
                if (reward is not CardReward)
                {
                    if (SoloRunController.IsActive)
                        await AiFlowControl.WaitWhilePausedAsync();

                    var rewardName = reward.GetType().Name;
                    log.Log("REWARD", $"Auto-claim {rewardName}");
                    log.Log("AI_ACTION", $"领取奖励: {rewardName}");
                    try
                    {
                        await reward.SelectUnsynchronized();
                        log.Log("REWARD", $"Claimed {rewardName}");
                    }
                    catch (Exception ex)
                    {
                        log.LogError("REWARD", $"Failed to claim {rewardName}: {ex.Message}");
                    }
                }
            }

            // Step 3: Handle card rewards with AI decision
            var cardRewards = rewards.OfType<CardReward>().ToList();
            log.Log("REWARD", $"Card rewards: {cardRewards.Count}");

            foreach (var cardReward in cardRewards)
            {
                var cards = cardReward.Cards.ToList();
                if (cards.Count == 0)
                {
                    // Don't call SelectUnsynchronized on an empty CardReward: OnSelect opens the
                    // native card-selection screen, which has nothing to show for the AI.
                    log.Log("REWARD", "Card reward has no cards, skipping");
                    cardReward.MarkContentAsSeen();
                    continue;
                }

                log.Log("REWARD", $"Card reward: {cards.Count} choices");

                if (SoloRunController.IsActive)
                    await AiFlowControl.WaitWhilePausedAsync();

                // Ask AI backend to choose
                int pick;
                string reason;
                try
                {
                    var cardModels = cards.Cast<CardModel>().ToList();
                    var (p, r) = await manager.DecisionService.DecideCardRewardAsync(aiPlayer, cardModels, CancellationToken.None);
                    pick = p;
                    reason = r;
                    if (!string.IsNullOrWhiteSpace(reason))
                        log.Log("AI_THINK", $"[选牌] {reason}");
                    log.Log("REWARD", $"AI picked index={pick}");
                }
                catch (Exception ex)
                {
                    log.LogError("REWARD", $"AI card decision failed: {ex.Message}, defaulting to 0");
                    pick = 0;
                    reason = "决策失败，默认选第一张";
                }

                if (pick >= 0 && pick < cards.Count)
                {
                    var chosenCard = cards[pick];
                    log.Log("AI_ACTION", $"选牌: {chosenCard.Id} | {reason}");
                    log.Log("REWARD", $"AI chose card: {chosenCard.Id}");

                    try
                    {
                        // Add card to deck
                        var result = await CardPileCmd.Add(chosenCard, PileType.Deck);
                        if (result.success)
                        {
                            log.Log("REWARD", $"Card {result.cardAdded.Id} added to deck");

                            // CardPileCmd.Add only raises CardAddFinished when the card has an on-table
                            // node/tween. The vanilla reward screen provides one; our suppressed flow does
                            // not, so the top-bar deck counter (_countLabel) would keep the old number
                            // forever while the deck view (ContentsChanged) already shows the new card.
                            try
                            {
                                PileType.Deck.GetPile(result.cardAdded.Owner).InvokeCardAddFinished();
                            }
                            catch (Exception refreshEx)
                            {
                                log.Log("REWARD", $"Deck counter refresh failed: {refreshEx.Message}");
                            }

                            // Local-only: RewardSynchronizer stamps the sender's own NetId, so
                            // broadcasting the AI's card would add it to the *host human's* deck on
                            // the other peers. The AI state snapshot is the correct carrier.
                        }
                        else
                        {
                            log.Log("REWARD", "Card add failed, skipping card reward");
                        }
                    }
                    catch (Exception ex)
                    {
                        log.LogError("REWARD", $"Card add error: {ex.Message}");
                    }
                }
                else
                {
                    // Skip all cards
                    log.Log("AI_ACTION", $"跳过选牌 | {reason}");
                    log.Log("REWARD", "AI chose to skip card reward");
                }

                // Fire after-reward hook
                try { await Hook.AfterRewardTaken(aiPlayer.RunState, aiPlayer, cardReward); }
                catch (Exception ex) { log.Log("REWARD", $"AfterRewardTaken hook error: {ex.Message}"); }

                cardReward.MarkContentAsSeen();
            }

            log.Log("REWARD", "AI reward processing complete");
            log.Log("AI_ACTION", "战斗奖励处理完成");

            // The host owns this flow; push the final AI state (deck/gold/relics/potions/grab bag)
            // to the clients so their local copies match before the next checksum.
            Compat.AiStateSync.BroadcastFromAuthority("rewards");

            if (SoloRunController.IsActive)
            {
                // Room-end rewards normally end with the rewards screen's proceed button.
                // Our interception suppresses that screen, so perform the step ourselves.
                if (rewardsSet.Room != null)
                {
                    await AiFlowControl.WaitWhilePausedAsync();
                    await SoloRoomAutomation.ProceedAfterRoomRewardsAsync(manager);
                }
            }
            else
            {
                // Signal AI player is ready to proceed to next act.
                // In multiplayer, the ActChangeSynchronizer waits for ALL players
                // to signal ready — without this the game hangs at "waiting for players".
                try
                {
                    if (RunManager.Instance.ActChangeSynchronizer != null)
                    {
                        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(
                            Compat.GameCompat.CreateVoteToMoveToNextAct(aiPlayer));
                        log.Log("REWARD", "AI player voted to proceed to next act");
                    }
                }
                catch (Exception ex)
                {
                    log.LogError("REWARD", $"Failed to signal AI ready for next act: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            log.LogError("REWARD", $"Fatal error: {ex.Message}\n{ex.StackTrace}");
        }
    }
}

// ---- Shop ----
[HarmonyPatch(typeof(MerchantRoom), nameof(MerchantRoom.EnterInternal))]
public static class MerchantRoomPatch
{
    [HarmonyPostfix]
    public static void AfterEnter()
    {
        var manager = AiTeammateManager.Instance;
        if (!manager.IsInitialized || manager.AiPlayer == null) return;

        var aiPlayer = manager.AiPlayer!;

        // Solo climb: buy through the room's own local inventory so the UI stays in sync.
        if (SoloRunController.IsActive)
        {
            Compat.MainThread.Post(() => SoloRoomAutomation.HandleShop(manager, aiPlayer));
            return;
        }

        // Multiplayer: the shop stock is rolled independently on every peer and purchases mutate the
        // AI's gold/deck/relics, so only the host may buy. Clients receive the final state snapshot.
        if (!Compat.AiMultiplayer.IsAuthority)
        {
            manager.AiLog.Log("SHOP", "非房主，跳过 AI 购物（由房主结算并同步状态）");
            return;
        }

        Compat.MainThread.Post(() => _ = RunShopAsync(manager, aiPlayer));
    }

    private static async Task RunShopAsync(AiTeammateManager manager, Player aiPlayer)
    {
        try
        {
            await Task.Delay(2000);
            await manager.OutOfCombatExecutor.AutoShop(aiPlayer, CancellationToken.None);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"Shop error: {ex.Message}");
        }
        finally
        {
            // Shop stock is rolled per peer from the AI's Shop/Rewards RNG; only the host buys, so
            // push the resulting gold/deck/relics/potions to the clients.
            Compat.AiStateSync.BroadcastFromAuthority("shop");
        }
    }
}

// ---- Map Navigation ----
[HarmonyPatch(typeof(NMapScreen), "_Ready")]
public static class MapScreenPatch
{
    [HarmonyPostfix]
    public static void AfterReady()
    {
        var manager = AiTeammateManager.Instance;
        if (!manager.IsInitialized) return;

        SoloRunController.TryBind(manager);
        if (manager.AiPlayer == null) return;

        var aiPlayer = manager.AiPlayer!;

        if (SoloRunController.IsActive)
        {
            SoloMapNavigator.OnMapOpened();
            return;
        }

        Compat.MainThread.Post(() => _ = RequestMapVoteAsync(aiPlayer));
    }

    /// <summary>
    /// Multiplayer: let the AI decide its own map vote shortly after the map opens. This used to
    /// click a node, which votes as the host/local player; only the AI's own slot votes now.
    /// </summary>
    private static async Task RequestMapVoteAsync(Player aiPlayer)
    {
        try
        {
            await Task.Delay(1500);
            var runState = RunManager.Instance?.DebugOnlyGetState();
            var synchronizer = RunManager.Instance?.MapSelectionSynchronizer;
            if (runState == null || synchronizer == null) return;

            AiMapVoter.Request(aiPlayer, synchronizer, runState.MapLocation, fallback: null);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"Map vote error: {ex.Message}");
        }
    }
}

// Solo climb: the map reopens after every room, so pick a route each time it opens.
[HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen.Open))]
public static class MapScreenOpenPatch
{
    [HarmonyPostfix]
    public static void AfterOpen()
    {
        SoloMapNavigator.OnMapOpened();
    }
}

// Solo climb: re-bind the AI as soon as a run scene exists. Both new runs and loaded saves create
// NRun after RunManager.Launch() set the local net id, so this is the most reliable place to
// restore control after "Continue" — a single bind attempt during loading can fire too early.
[HarmonyPatch(typeof(NRun), nameof(NRun._Ready))]
public static class RunReadySoloBindPatch
{
    [HarmonyPostfix]
    public static void AfterReady()
    {
        var manager = AiTeammateManager.Instance;
        if (!manager.TryInitialize()) return;

        // Multiplayer AI teammate: a loaded save rebuilt every Player instance, so the cached one
        // must be replaced before the first room runs. This is the earliest point where the live
        // RunState is guaranteed to exist.
        manager.DetectAndBindAiPlayer();

        if (!SoloRunController.TryBind(manager))
            SoloRunController.RequestBindRetry("NRun._Ready");
    }
}

// ---- Rest Site: BeginRestSite creates options for all players ----
[HarmonyPatch(typeof(RestSiteSynchronizer), nameof(RestSiteSynchronizer.BeginRestSite))]
public static class RestSiteBeginPatch
{
    [HarmonyPostfix]
    public static void AfterBeginRestSite()
    {
        var manager = AiTeammateManager.Instance;
        if (!manager.IsInitialized || manager.AiPlayer == null) return;

        var aiPlayer = manager.AiPlayer!;
        var sync = RunManager.Instance?.RestSiteSynchronizer;
        if (sync == null) return;

        // Solo climb: the AI is the local player, so use the normal local-choice path.
        if (SoloRunController.IsActive)
        {
            SoloRoomAutomation.HandleRestSite(sync, aiPlayer, manager);
            return;
        }

        var options = sync.GetOptionsForPlayer(aiPlayer);
        if (options == null || options.Count == 0) return;

        var optionIds = options.Select(o => o.OptionId).ToList();
        var creature = aiPlayer.Creature;
        var currentHp = creature.CurrentHp;
        var maxHp = creature.MaxHp;
        MainFile.Logger.Info($"[REST] AI rest options: {string.Join(", ", optionIds)}");

        // Pre-compute HP-safe fallback locally (fast, no LLM needed).
        var hpPct = maxHp > 0 ? (currentHp * 100 / maxHp) : 100;
        int healIdx = optionIds.IndexOf("HEAL");
        int smithIdx = optionIds.IndexOf("SMITH");
        int mendIdx = optionIds.IndexOf("MEND");
        if (healIdx < 0) healIdx = 0;
        if (smithIdx < 0) smithIdx = optionIds.Count > 1 ? 1 : 0;

        int safeFallback;
        if (hpPct <= 20) safeFallback = healIdx;
        else if (hpPct <= 35) safeFallback = healIdx; // HEAL (not MEND) — AI needs its own HP
        else if (hpPct <= 50) safeFallback = healIdx;
        else safeFallback = smithIdx;

        // Clamp to valid range
        safeFallback = Math.Clamp(safeFallback, 0, options.Count - 1);
        var localFallbackOption = options[safeFallback];
        if (!localFallbackOption.IsEnabled)
        {
            for (int i = 0; i < options.Count; i++)
                if (options[i].IsEnabled) { safeFallback = i; break; }
        }

        var fallbackReason = $"HP{hpPct}%，快速安全策略（{localFallbackOption.OptionId}）";
        MainFile.Logger.Info($"[REST] AI fallback prepared: {localFallbackOption.OptionId}");

        // Let the LLM make the actual decision, including SMITH upgrade indices.
        // If it times out or returns invalid data, fall back to the safe local rule.
        // Multiplayer: only the host asks the model; every peer applies the same choice so the spot
        // heals/upgrades identically and each peer's rest-site synchronizer completes the AI's slot.
        var restKey = Compat.AiChoiceCodec.Key("rest", aiPlayer.RunState?.TotalFloor ?? 0, string.Join(",", optionIds));
        _ = Task.Run(async () =>
        {
            RestSiteDecisionResult decision;
            if (Compat.AiMultiplayer.RequiresDecisionSync && Compat.AiMultiplayer.IsFollower)
            {
                Compat.AiSyncBus.EnsureRegistered();
                var payload = await Compat.AiSyncBus.AwaitAsync(
                    Compat.AiSyncBus.KindChoice, restKey, TimeSpan.FromSeconds(20));
                var dto = Compat.AiChoiceCodec.Deserialize(payload);
                if (dto != null)
                {
                    // Prefer the stable option id: it survives any difference in option ordering.
                    var byId = -1;
                    if (!string.IsNullOrWhiteSpace(dto.id))
                    {
                        for (int oi = 0; oi < options.Count; oi++)
                        {
                            if (string.Equals(options[oi].OptionId, dto.id, StringComparison.Ordinal))
                            {
                                byId = oi;
                                break;
                            }
                        }
                    }
                    var resolved = byId >= 0 ? byId : dto.index;
                    decision = resolved >= 0
                        ? new RestSiteDecisionResult
                        {
                            Choice = resolved,
                            Reason = string.IsNullOrWhiteSpace(dto.reason) ? "跟随房主决策" : dto.reason,
                            UpgradeIndices = dto.indices ?? new List<int>()
                        }
                        : new RestSiteDecisionResult { Choice = safeFallback, Reason = fallbackReason };
                }
                else
                {
                    decision = new RestSiteDecisionResult { Choice = safeFallback, Reason = fallbackReason };
                }
            }
            else
            {
                try
                {
                    using var cts = new CancellationTokenSource(5000); // 5s max for diagnostics
                    decision = await manager.DecisionService.DecideRestSiteAsync(
                        aiPlayer, currentHp, maxHp, options, cts.Token);
                }
                catch
                {
                    decision = new RestSiteDecisionResult
                    {
                        Choice = safeFallback,
                        Reason = fallbackReason
                    };
                }

                if (Compat.AiMultiplayer.RequiresDecisionSync)
                {
                    Compat.AiSyncBus.Publish(
                        Compat.AiSyncBus.KindChoice,
                        restKey,
                        Compat.AiChoiceCodec.Serialize(decision.Choice, decision.UpgradeIndices, decision.Reason,
                            options[Math.Clamp(decision.Choice, 0, options.Count - 1)].OptionId));
                }
            }

            var applyPick = decision.Choice;
            if (applyPick < 0 || applyPick >= options.Count || !options[applyPick].IsEnabled)
                applyPick = safeFallback;

            var applyReason = string.IsNullOrWhiteSpace(decision.Reason) ? fallbackReason : decision.Reason;
            var chosenOption = options[applyPick];

            manager.AiLog.Log("AI_THINK", $"[休息站] {applyReason}");
            manager.AiLog.Log("AI_ACTION", $"休息站选择: {chosenOption.OptionId} | {applyReason}");
            MainFile.Logger.Info($"[REST] AI chose {chosenOption.OptionId} via {(decision.Choice == safeFallback ? "fallback/LLM" : "LLM")}");

            if (chosenOption.OptionId == "SMITH" && decision.UpgradeIndices.Count > 0)
            {
                manager.SetPendingRestSiteUpgradeIndices(decision.UpgradeIndices);
                MainFile.Logger.Info($"[REST] Pending SMITH upgrade indices: {string.Join(",", decision.UpgradeIndices)}");
            }
            else
            {
                manager.SetPendingRestSiteUpgradeIndices(null);
            }

            Compat.MainThread.Post(() => _ = ApplyRestSiteChoiceAsync(sync, aiPlayer, options, applyPick, manager));
        });
    }

    /// <summary>
    /// Execute the rest site choice for the AI player.
    /// OnSelect() on RestSiteOption (e.g. HealRestSiteOption) internally calls
    /// Hook.AfterRestSiteHeal + RewardsCmd.OfferCustom which NRE for AI players
    /// because their RunState/UnlockState aren't fully initialized.
    /// Instead, we apply the effect directly (heal HP, etc.) then manually
    /// update the synchronizer state so the game UI shows the AI has chosen.
    /// </summary>
    private static async Task ApplyRestSiteChoiceAsync(RestSiteSynchronizer sync, Player aiPlayer,
        IReadOnlyList<MegaCrit.Sts2.Core.Entities.RestSite.RestSiteOption> options, int pick,
        AiTeammateManager manager)
    {
        var chosen = options[pick];
        var log = manager.AiLog;
        bool effectApplied = false;

        try
        {
            // Step 1: Apply the actual effect directly (bypass OnSelect's hooks)
            switch (chosen.OptionId)
            {
                case "HEAL":
                {
                    var creature = aiPlayer.Creature;
                    if (creature == null)
                    {
                        log.LogError("REST", "AI creature is null, cannot heal");
                        return;
                    }
                    int healAmount = (int)(creature.MaxHp * 0.3m);
                    int oldHp = creature.CurrentHp;
                    await MegaCrit.Sts2.Core.Commands.CreatureCmd.Heal(creature, healAmount, playAnim: false);
                    effectApplied = true;
                    log.Log("REST", $"Direct heal: {oldHp} → {creature.CurrentHp}/{creature.MaxHp} (+{healAmount})");
                    MainFile.Logger.Info($"[REST] AI healed: {oldHp} → {creature.CurrentHp}/{creature.MaxHp}");
                    break;
                }
                case "MEND":
                {
                    // MEND heals OTHER players and vanilla opens a target picker that our
                    // auto-pick can't drive, so apply the effect directly to the most
                    // endangered teammate.
                    // Do NOT enumerate CombatManager's state here: it is reset to null as soon
                    // as the previous combat ends, so at a rest site it is always null and MEND
                    // silently turned into a no-op. The run state always has every player.
                    var teammates = aiPlayer.RunState?.Players
                        .Where(p => p != aiPlayer && p.Creature != null && p.Creature.IsAlive)
                        .OrderBy(p => (double)p.Creature.CurrentHp / Math.Max(1, p.Creature.MaxHp))
                        .ThenBy(p => p.Creature.CurrentHp)
                        .ToList() ?? new List<Player>();

                    if (teammates.Count == 0)
                    {
                        log.Log("REST", "MEND: no alive teammates to heal");
                        break;
                    }

                    // Heal the most endangered teammate
                    var target = teammates[0];
                    var targetCreature = target.Creature;
                    decimal mendAmount;
                    try
                    {
                        // Same amount vanilla MEND heals, including relic modifiers (Regal Pillow).
                        mendAmount = MegaCrit.Sts2.Core.Entities.RestSite.MendRestSiteOption.GetHealAmount(target);
                    }
                    catch
                    {
                        mendAmount = MegaCrit.Sts2.Core.Entities.RestSite.HealRestSiteOption.GetBaseHealAmount(targetCreature);
                    }

                    int oldTargetHp = targetCreature.CurrentHp;
                    await MegaCrit.Sts2.Core.Commands.CreatureCmd.Heal(targetCreature, mendAmount, playAnim: false);
                    try
                    {
                        // Vanilla MEND runs the heal hooks for the target too (Stone Humidifier
                        // max HP, Regal Pillow flash, Night Terrors). Unlike the AI-targeted HEAL
                        // branch above, the target here is a fully initialized human player.
                        await Hook.AfterRestSiteHeal(target.RunState, target, isMimicked: false);
                    }
                    catch (Exception ex)
                    {
                        MainFile.Logger.Warn($"[REST] MEND AfterRestSiteHeal failed: {ex.Message}");
                    }

                    effectApplied = true;
                    var teammateName = target.Character?.Id.Entry ?? "Teammate";
                    log.Log("REST", $"MEND healed teammate {teammateName}: {oldTargetHp} → {targetCreature.CurrentHp}/{targetCreature.MaxHp} (+{mendAmount})");
                    MainFile.Logger.Info($"[REST] AI mended {teammateName}: {oldTargetHp} → {targetCreature.CurrentHp}/{targetCreature.MaxHp}");
                    break;
                }
                case "SMITH":
                    // Upgrade options open a card selection screen; our ChooseACardSelectionPatch
                    // auto-picks a card and handles the upgrade.  Hook.AfterRestSiteSmith
                    // may NRE for AI (missing RunState entries) but the card upgrade
                    // already completed before that point.
                    try
                    {
                        log.Log("REST", $"Calling OnSelect for {chosen.OptionId}");
                        // Timeout after 15s to prevent hanging if card selection auto-pick fails
                        var onSelectTask = chosen.OnSelect();
                        var timeoutTask = Task.Delay(15_000);
                        var completed = await Task.WhenAny(onSelectTask, timeoutTask);
                        if (completed == timeoutTask)
                        {
                            log.Log("REST", $"{chosen.OptionId} OnSelect timed out — upgrade may still have been applied");
                        }
                        else
                        {
                            await onSelectTask; // propagate any exception
                        }
                        effectApplied = true;
                        log.Log("REST", $"{chosen.OptionId} upgrade completed");
                    }
                    catch (NullReferenceException)
                    {
                        // Hook.AfterRestSiteSmith or VFX may NRE — upgrade already applied
                        effectApplied = true;
                        log.Log("REST", $"{chosen.OptionId} OnSelect NRE (expected) — upgrade should be done");
                    }
                    catch (Exception ex)
                    {
                        // Card selection may have already applied the upgrade via patch
                        effectApplied = true;
                        log.Log("REST", $"{chosen.OptionId} OnSelect error: {ex.Message} — upgrade may still be done");
                    }
                    break;
                default:
                    // LIFT, DIG, etc.
                    try
                    {
                        log.Log("REST", $"Trying OnSelect for {chosen.OptionId}");
                        var onSelectTask = chosen.OnSelect();
                        var timeoutTask = Task.Delay(15_000);
                        var completed = await Task.WhenAny(onSelectTask, timeoutTask);
                        if (completed == timeoutTask)
                        {
                            log.Log("REST", $"{chosen.OptionId} OnSelect timed out");
                        }
                        else
                        {
                            await onSelectTask;
                            effectApplied = true;
                        }
                        log.Log("REST", $"OnSelect completed for {chosen.OptionId}");
                    }
                    catch (NullReferenceException)
                    {
                        log.Log("REST", $"OnSelect NRE for {chosen.OptionId}, action may be incomplete");
                    }
                    break;
            }

            // Step 2: Update synchronizer state so the UI shows the AI has chosen.
            // ChooseOption (private) calls option.OnSelect() internally which NREs.
            // We bypass that and directly set lastChosenOptionIndex + fire events.
            UpdateSyncStateDirectly(sync, aiPlayer, options, chosen, pick, effectApplied, log);
        }
        catch (Exception ex)
        {
            log.LogError("REST", $"ApplyRestSiteChoice error: {ex.Message}");
            MainFile.Logger.Error($"[REST] ApplyRestSiteChoice error: {ex.Message}");
        }
    }

    /// <summary>
    /// Manually update RestSiteSynchronizer internal state for the AI player,
    /// without going through ChooseOption (which calls OnSelect and NREs).
    /// Sets lastChosenOptionIndex and fires AfterPlayerOptionChosen so the
    /// rest site UI shows the "completed" indicator.
    /// </summary>
    private static void UpdateSyncStateDirectly(RestSiteSynchronizer sync, Player aiPlayer,
        IReadOnlyList<MegaCrit.Sts2.Core.Entities.RestSite.RestSiteOption> options,
        MegaCrit.Sts2.Core.Entities.RestSite.RestSiteOption chosenOption, int pick,
        bool effectApplied, AiLogger log)
    {
        try
        {
            // Access _restSites (List<PlayerRestSite>) via Traverse
            var restSitesTraverse = Traverse.Create(sync).Field("_restSites");
            var restSites = restSitesTraverse.GetValue();
            if (restSites == null)
            {
                log.Log("REST", "_restSites is null, cannot update sync state");
                return;
            }

            // Find the PlayerRestSite whose options list matches ours
            var restSitesList = (System.Collections.IList)restSites;
            object? targetSite = null;
            foreach (var site in restSitesList)
            {
                if (site == null) continue;
                var siteOptions = Traverse.Create(site).Field("options").GetValue();
                if (ReferenceEquals(siteOptions, options))
                {
                    targetSite = site;
                    break;
                }
            }

            if (targetSite == null)
            {
                log.Log("REST", "Could not find AI player's rest site entry in _restSites");
                return;
            }

            // Set lastChosenOptionIndex
            var lastIndexTraverse = Traverse.Create(targetSite).Field("lastChosenOptionIndex");
            lastIndexTraverse.SetValue((uint?)pick);
            log.Log("REST", $"Sync state updated: lastChosenOptionIndex = {pick}");

            // Fire AfterPlayerOptionChosen event so the UI refreshes
            try
            {
                var afterEvent = (Action<MegaCrit.Sts2.Core.Entities.RestSite.RestSiteOption, bool, ulong>?)
                    Traverse.Create(sync).Field("AfterPlayerOptionChosen").GetValue();
                afterEvent?.Invoke(chosenOption, effectApplied, aiPlayer.NetId);
                log.Log("REST", "AfterPlayerOptionChosen fired");
            }
            catch (Exception ex)
            {
                MainFile.Logger.Warn($"[REST] AfterPlayerOptionChosen fire failed: {ex.Message}");
            }

            // Fire BeforePlayerOptionChosen for completeness
            try
            {
                var beforeEvent = (Action<MegaCrit.Sts2.Core.Entities.RestSite.RestSiteOption, ulong>?)
                    Traverse.Create(sync).Field("BeforePlayerOptionChosen").GetValue();
                beforeEvent?.Invoke(chosenOption, aiPlayer.NetId);
            }
            catch { /* optional */ }

            // 0.111: RestSiteRoom.Exit awaits AfterAllRestSitesCompleted(), which awaits every
            // PlayerRestSite completion source. The AI has no net peer, so nothing else ever completes
            // its slot — without this the room exit hangs and the next map travel shows a black screen.
            GameCompat.CompleteAiRestSiteSlot(sync, aiPlayer);
            log.Log("REST", "AI rest site slot completed");
        }
        catch (Exception ex)
        {
            log.Log("REST", $"UpdateSyncStateDirectly error: {ex.Message}");
            MainFile.Logger.Warn($"[REST] UpdateSyncStateDirectly: {ex.Message}");
        }
    }
}

// ---- Safety net: a room exit must never wait forever on the AI's rest-site slot ----
// Reached when the choice path failed to find the AI's PlayerRestSite entry (or the human travelled
// while the AI's decision was still in flight). Without it, RestSiteRoom.Exit awaits forever and the
// game sits on the black fade-out.
[HarmonyPatch(typeof(RestSiteSynchronizer), "AfterAllRestSitesCompleted")]
public static class RestSiteCompletionSafetyPatch
{
    [HarmonyPrefix]
    public static void Prefix()
    {
        var manager = AiTeammateManager.Instance;
        if (!manager.IsInitialized || manager.AiPlayer == null) return;

        // Solo climb: the AI owns the local player, so the vanilla local-player path completes the slot.
        if (SoloRunController.IsActive) return;

        var sync = RunManager.Instance?.RestSiteSynchronizer;
        if (sync == null) return;

        GameCompat.CompleteAiRestSiteSlot(sync, manager.AiPlayer);
    }
}

// ---- Fix: prevent AI from hanging on card selections ----
// CardSelectCmd.ShouldSelectLocalCard returns false for AI, causing WaitForRemoteChoice (softlock)
[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Commands.CardSelectCmd), "ShouldSelectLocalCard")]
public static class ShouldSelectLocalCardPatch
{
    [HarmonyPostfix]
    public static void Postfix(Player player, ref bool __result)
    {
        if (__result) return;
        var manager = AiTeammateManager.Instance;
        if (!manager.IsInitialized || manager.AiPlayer == null) return;
        if (player.NetId == manager.AiPlayer.NetId)
            __result = true;
    }
}

internal static class AiPlayerChoiceContextHelper
{
    public static async Task EndChoiceAsync(PlayerChoiceContext context, Player player)
    {
        var manager = AiTeammateManager.Instance;
        if (manager.IsInitialized
            && manager.AiPlayer != null
            && player.NetId == manager.AiPlayer.NetId
            && context is GameActionPlayerChoiceContext gameActionContext
            && gameActionContext.Action.OwnerId == player.NetId)
        {
            RunManager.Instance.ActionQueueSynchronizer.RequestResumeActionAfterPlayerChoice(gameActionContext.Action);
            await gameActionContext.Action.WaitForActionToResumeExecutingAfterPlayerChoice();
            return;
        }

        await context.SignalPlayerChoiceEnded();
    }
}

// ---- Intercept FromSimpleGrid for AI (e.g. Cleanse) ----
// Instead of letting the game create a card selection screen and then trying
// to auto-pick it, intercept the call before the screen is ever created.
[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Commands.CardSelectCmd), "FromChooseACardScreen")]
public static class FromChooseACardScreenAiPatch
{
    [HarmonyPrefix]
    public static bool Prefix(
        PlayerChoiceContext context,
        IReadOnlyList<CardModel> cards,
        Player player,
        bool canSkip,
        ref Task<CardModel?> __result)
    {
        var manager = AiTeammateManager.Instance;
        if (!manager.IsInitialized || manager.AiPlayer == null) return true;
        if (player.NetId != manager.AiPlayer.NetId) return true;

        __result = SelectChooseACardForAiAsync(context, cards, player, canSkip, manager);
        return false;
    }

    private static async Task<CardModel?> SelectChooseACardForAiAsync(
        PlayerChoiceContext context,
        IReadOnlyList<CardModel> cards,
        Player player,
        bool canSkip,
        AiTeammateManager manager)
    {
        if (cards.Count == 0)
            return null;

        var cardList = cards.ToList();
        var choiceId = RunManager.Instance.PlayerChoiceSynchronizer.ReserveChoiceId(player);
        await Compat.GameCompat.SignalPlayerChoiceBegun(context, player, PlayerChoiceOptions.None);
        try
        {
            CardModel? selected = null;
            if (cardList.Count == 1 && !canSkip)
            {
                selected = cardList[0];
            }
            else
            {
                selected = CardGridSelectionPatch
                    .PickCardsByHeuristic(cardList, 1, 1, "FromChooseACardScreen")
                    .FirstOrDefault();
            }

            var selectedName = selected?.Title ?? selected?.Id.Entry ?? "(跳过)";
            MainFile.Logger.Info($"[AI] FromChooseACardScreen pick: [{selectedName}]");
            manager.AiLog.Log("AI_ACTION", $"选牌: {selectedName} | 启发式");

            var index = selected == null ? -1 : cardList.IndexOf(selected);
            RunManager.Instance.PlayerChoiceSynchronizer.SyncLocalChoice(
                player,
                choiceId,
                PlayerChoiceResult.FromIndex(index));

            return selected;
        }
        finally
        {
            await AiPlayerChoiceContextHelper.EndChoiceAsync(context, player);
        }
    }
}

[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Commands.CardSelectCmd), "FromSimpleGrid")]
public static class FromSimpleGridAiPatch
{
    [HarmonyPrefix]
    public static bool Prefix(PlayerChoiceContext context, IReadOnlyList<CardModel> cardsIn,
        Player player, CardSelectorPrefs prefs, ref Task<IEnumerable<CardModel>> __result)
    {
        var manager = AiTeammateManager.Instance;
        if (!manager.IsInitialized || manager.AiPlayer == null) return true;
        if (player.NetId != manager.AiPlayer.NetId) return true;

        // Handle AI card selection directly — no screen needed
        MainFile.Logger.Info($"[AI] Intercepting FromSimpleGrid for AI: {cardsIn.Count} cards, min={prefs.MinSelect} max={prefs.MaxSelect}");
        var cards = cardsIn.ToList();
        var picked = CardGridSelectionPatch.PickCardsByHeuristic(cards, prefs.MinSelect, prefs.MaxSelect, "FromSimpleGrid");
        var names = string.Join(", ", picked.Select(c => c.Title ?? c.Id.Entry));
        MainFile.Logger.Info($"[AI] FromSimpleGrid pick: [{names}]");
        manager.AiLog.Log("AI_ACTION", $"选牌: {names} | 启发式");

        // Sync the choice
        var choiceId = RunManager.Instance.PlayerChoiceSynchronizer.ReserveChoiceId(player);
        var indexes = picked.Select(c => cards.IndexOf(c)).ToList();
        RunManager.Instance.PlayerChoiceSynchronizer.SyncLocalChoice(player, choiceId,
            PlayerChoiceResult.FromIndexes(indexes));

        __result = Task.FromResult((IEnumerable<CardModel>)picked);
        return false; // skip original
    }
}

// ---- Intercept FromDeckGeneric for AI (e.g. card removal from deck) ----
[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Commands.CardSelectCmd), "FromDeckGeneric")]
public static class FromDeckGenericAiPatch
{
    [HarmonyPrefix]
    public static bool Prefix(Player player, CardSelectorPrefs prefs,
        Func<CardModel, bool>? filter, Func<CardModel, int>? sortingOrder,
        ref Task<IEnumerable<CardModel>> __result)
    {
        var manager = AiTeammateManager.Instance;
        if (!manager.IsInitialized || manager.AiPlayer == null) return true;
        if (player.NetId != manager.AiPlayer.NetId) return true;

        // Build the card list the same way as the original method
        var source = PileType.Deck.GetPile(player).Cards.ToList();
        var list = (filter == null) ? source.ToList() : source.Where(filter).ToList();
        if (sortingOrder != null) list = list.OrderBy(sortingOrder).ToList();
        if (list.Count == 0)
        {
            __result = Task.FromResult(Enumerable.Empty<CardModel>());
            return false;
        }

        MainFile.Logger.Info($"[AI] Intercepting FromDeckGeneric for AI: {list.Count} cards, min={prefs.MinSelect} max={prefs.MaxSelect}");
        var picked = CardGridSelectionPatch.PickCardsByHeuristic(list, prefs.MinSelect, prefs.MaxSelect, "FromDeckGeneric");
        var names = string.Join(", ", picked.Select(c => c.Title ?? c.Id.Entry));
        MainFile.Logger.Info($"[AI] FromDeckGeneric pick: [{names}]");
        manager.AiLog.Log("AI_ACTION", $"选牌: {names} | 启发式");

        var choiceId = RunManager.Instance.PlayerChoiceSynchronizer.ReserveChoiceId(player);
        RunManager.Instance.PlayerChoiceSynchronizer.SyncLocalChoice(player, choiceId,
            PlayerChoiceResult.FromMutableDeckCards(picked));

        __result = Task.FromResult((IEnumerable<CardModel>)picked);
        return false; // skip original
    }
}

// ---- Intercept FromDeckForUpgrade for AI (rest site SMITH, upgrade events/relics) ----
[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Commands.CardSelectCmd), "FromDeckForUpgrade")]
public static class FromDeckForUpgradeAiPatch
{
    [HarmonyPrefix]
    public static bool Prefix(Player player, CardSelectorPrefs prefs, ref Task<IEnumerable<CardModel>> __result)
    {
        var manager = AiTeammateManager.Instance;
        if (!manager.IsInitialized || manager.AiPlayer == null) return true;
        if (player.NetId != manager.AiPlayer.NetId) return true;

        var cards = PileType.Deck.GetPile(player).Cards
            .Where(c => c.IsUpgradable)
            .ToList();

        if (cards.Count == 0)
        {
            __result = Task.FromResult(Enumerable.Empty<CardModel>());
            return false;
        }

        var pendingIndices = manager.ConsumePendingRestSiteUpgradeIndices();
        List<CardModel> picked;
        string reason;

        if (pendingIndices is { Count: > 0 })
        {
            picked = pendingIndices
                .Where(i => i >= 0 && i < cards.Count)
                .Distinct()
                .Take(Math.Max(1, prefs.MaxSelect))
                .Select(i => cards[i])
                .ToList();
            reason = "休息站 LLM 选牌";
        }
        else
        {
            picked = CardGridSelectionPatch.PickCardsByHeuristic(cards, prefs.MinSelect, prefs.MaxSelect, "FromDeckForUpgrade");
            reason = "启发式";
        }

        if (picked.Count < prefs.MinSelect)
        {
            var fallback = CardGridSelectionPatch.PickCardsByHeuristic(cards, prefs.MinSelect, prefs.MaxSelect, "FromDeckForUpgradeFallback");
            foreach (var card in fallback)
            {
                if (picked.Contains(card)) continue;
                picked.Add(card);
                if (picked.Count >= prefs.MinSelect) break;
            }
        }

        if (picked.Count > prefs.MaxSelect)
            picked = picked.Take(prefs.MaxSelect).ToList();

        var names = string.Join(", ", picked.Select(c => c.Title ?? c.Id.Entry));
        MainFile.Logger.Info($"[AI] FromDeckForUpgrade pick: [{names}] | {reason}");
        manager.AiLog.Log("AI_ACTION", $"升级选牌: {names} | {reason}");

        var choiceId = RunManager.Instance.PlayerChoiceSynchronizer.ReserveChoiceId(player);
        RunManager.Instance.PlayerChoiceSynchronizer.SyncLocalChoice(player, choiceId,
            PlayerChoiceResult.FromMutableDeckCards(picked));

        __result = Task.FromResult((IEnumerable<CardModel>)picked);
        return false;
    }
}

// ---- Intercept FromDeckForEnchantment for AI (events/relics/custom mod enchant flows) ----
[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Commands.CardSelectCmd), nameof(MegaCrit.Sts2.Core.Commands.CardSelectCmd.FromDeckForEnchantment),
    new[] { typeof(Player), typeof(EnchantmentModel), typeof(int), typeof(Func<CardModel, bool>), typeof(CardSelectorPrefs) })]
public static class FromDeckForEnchantmentAiPatch
{
    [HarmonyPrefix]
    public static bool Prefix(
        Player player,
        EnchantmentModel enchantment,
        int amount,
        Func<CardModel, bool>? additionalFilter,
        CardSelectorPrefs prefs,
        ref Task<IEnumerable<CardModel>> __result)
    {
        var manager = AiTeammateManager.Instance;
        if (!manager.IsInitialized || manager.AiPlayer == null) return true;
        if (player.NetId != manager.AiPlayer.NetId) return true;

        __result = SelectDeckEnchantForAiAsync(player, enchantment, amount, additionalFilter, prefs, manager);
        return false;
    }

    private static async Task<IEnumerable<CardModel>> SelectDeckEnchantForAiAsync(
        Player player,
        EnchantmentModel enchantment,
        int amount,
        Func<CardModel, bool>? additionalFilter,
        CardSelectorPrefs prefs,
        AiTeammateManager manager)
    {
        var deck = PileType.Deck.GetPile(player).Cards.ToList();
        var candidates = deck
            .Where(card => enchantment.CanEnchant(card) && (additionalFilter?.Invoke(card) ?? true))
            .ToList();

        if (candidates.Count == 0)
        {
            MainFile.Logger.Info($"[AI] FromDeckForEnchantment found no valid candidates for {enchantment.Id.Entry}");
            manager.AiLog.Log("AI_ACTION", $"附魔选牌: 无可选目标 | {enchantment.Id.Entry}");
            return Array.Empty<CardModel>();
        }

        if (candidates.Count <= prefs.MinSelect)
        {
            var forcedNames = string.Join(", ", candidates.Select(c => c.Title ?? c.Id.Entry));
            MainFile.Logger.Info($"[AI] FromDeckForEnchantment forced pick: [{forcedNames}]");
            manager.AiLog.Log("AI_ACTION", $"附魔选牌: {forcedNames} | 强制唯一候选");
            return candidates;
        }

        List<CardModel> picked;
        string reason;
        try
        {
            var (indices, llmReason) = await manager.DecisionService.DecideCardSelectionAsync(
                player,
                candidates,
                prefs.MinSelect,
                prefs.MaxSelect,
                "FromDeckForEnchantment",
                CancellationToken.None);

            picked = indices
                .Where(i => i >= 0 && i < candidates.Count)
                .Distinct()
                .Take(Math.Max(1, prefs.MaxSelect))
                .Select(i => candidates[i])
                .ToList();
            reason = llmReason;
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"AI enchant selection failed: {ex.Message}, falling back to heuristic");
            picked = new List<CardModel>();
            reason = "AI调用失败，启发式选择";
        }

        if (picked.Count < prefs.MinSelect)
        {
            var fallback = CardGridSelectionPatch.PickCardsByHeuristic(
                candidates,
                prefs.MinSelect,
                prefs.MaxSelect,
                "FromDeckForEnchantmentFallback");
            foreach (var card in fallback)
            {
                if (picked.Contains(card)) continue;
                picked.Add(card);
                if (picked.Count >= prefs.MinSelect) break;
            }
        }

        if (picked.Count > prefs.MaxSelect)
            picked = picked.Take(prefs.MaxSelect).ToList();

        var names = string.Join(", ", picked.Select(c => c.Title ?? c.Id.Entry));
        var choiceId = RunManager.Instance.PlayerChoiceSynchronizer.ReserveChoiceId(player);
        RunManager.Instance.PlayerChoiceSynchronizer.SyncLocalChoice(
            player,
            choiceId,
            PlayerChoiceResult.FromMutableDeckCards(picked));

        MainFile.Logger.Info($"[AI] FromDeckForEnchantment pick: [{names}] | enchant={enchantment.Id.Entry} amount={amount} reason={reason}");
        manager.AiLog.Log("AI_ACTION", $"附魔选牌: {names} | {reason}");
        return picked;
    }
}

// ---- Intercept FromHand for AI (e.g. BurningPact, Survivor, TrueGrit) ----
[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Commands.CardSelectCmd), "FromHand")]
public static class FromHandAiPatch
{
    [HarmonyPrefix]
    public static bool Prefix(
        PlayerChoiceContext context,
        Player player,
        CardSelectorPrefs prefs,
        Func<CardModel, bool>? filter,
        AbstractModel source,
        ref Task<IEnumerable<CardModel>> __result)
    {
        var manager = AiTeammateManager.Instance;
        if (!manager.IsInitialized) return true;

        // dsh owns the local player: never open the vanilla hand-selection UI (it soft-locks),
        // park a hand_select decision for the external agent instead.
        if (DshHandSelect.AppliesTo(player))
        {
            __result = SelectFromHandForDshAsync(context, player, prefs, filter, source, manager);
            return false;
        }

        if (manager.AiPlayer == null) return true;
        if (player.NetId != manager.AiPlayer.NetId) return true;

        __result = SelectFromHandForAiAsync(context, player, prefs, filter, source, manager);
        return false;
    }

    private static async Task<IEnumerable<CardModel>> SelectFromHandForDshAsync(
        PlayerChoiceContext context,
        Player player,
        CardSelectorPrefs prefs,
        Func<CardModel, bool>? filter,
        AbstractModel source,
        AiTeammateManager manager)
    {
        if (CombatManager.Instance.IsOverOrEnding)
            return Array.Empty<CardModel>();

        NPlayerHand.Instance?.CancelAllCardPlay();

        var cards = PileType.Hand.GetPile(player).Cards
            .Where(filter ?? (_ => true))
            .ToList();

        if (cards.Count == 0)
            return cards;

        if (!prefs.RequireManualConfirmation && cards.Count <= prefs.MinSelect)
            return cards;

        var choiceId = RunManager.Instance.PlayerChoiceSynchronizer.ReserveChoiceId(player);
        await Compat.GameCompat.SignalPlayerChoiceBegun(context, player, PlayerChoiceOptions.CancelPlayCardActions);
        try
        {
            var selectionTag = CardGridSelectionPatch.DescribeHandSelectionSource(source, prefs, "FromHand");
            var picked = await DshHandSelect.AskAsync(
                player, cards, prefs.MinSelect, prefs.MaxSelect, selectionTag,
                CancellationToken.None, parkForDsh: true);

            var names = picked.Count > 0
                ? string.Join(", ", picked.Select(c => c.Title ?? c.Id.Entry))
                : "(空)";

            MainFile.Logger.Info(
                $"[Agent] FromHand hand_select: [{names}] for {selectionTag} (min={prefs.MinSelect} max={prefs.MaxSelect})");
            manager.AiLog.Log("AI_ACTION", $"手牌选牌(dsh): {names} | {selectionTag}");

            RunManager.Instance.PlayerChoiceSynchronizer.SyncLocalChoice(
                player,
                choiceId,
                PlayerChoiceResult.FromMutableCombatCards(picked));

            return picked;
        }
        finally
        {
            await AiPlayerChoiceContextHelper.EndChoiceAsync(context, player);
        }
    }

    private static async Task<IEnumerable<CardModel>> SelectFromHandForAiAsync(
        PlayerChoiceContext context,
        Player player,
        CardSelectorPrefs prefs,
        Func<CardModel, bool>? filter,
        AbstractModel source,
        AiTeammateManager manager)
    {
        if (CombatManager.Instance.IsOverOrEnding)
            return Array.Empty<CardModel>();

        NPlayerHand.Instance?.CancelAllCardPlay();

        var cards = PileType.Hand.GetPile(player).Cards
            .Where(filter ?? (_ => true))
            .ToList();

        if (cards.Count == 0)
            return cards;

        if (!prefs.RequireManualConfirmation && cards.Count <= prefs.MinSelect)
            return cards;

        var choiceId = RunManager.Instance.PlayerChoiceSynchronizer.ReserveChoiceId(player);
        await Compat.GameCompat.SignalPlayerChoiceBegun(context, player, PlayerChoiceOptions.CancelPlayCardActions);
        try
        {
            var selectionTag = CardGridSelectionPatch.DescribeHandSelectionSource(source, prefs, "FromHand");
            // dsh is the AI's brain in a singleplayer solo climb: let it answer the AI's own hand
            // selections instead of silently resolving them with the heuristic. Multiplayer runs keep
            // the deterministic heuristic on every peer (see DshHandSelect.AskAsync).
            // 单人爬塔时 AI 自己的选牌也交给 dsh；多人局仍走启发式以保证各端一致。
            var picked = await DshHandSelect.AskAsync(
                player, cards, prefs.MinSelect, prefs.MaxSelect, selectionTag,
                CancellationToken.None, parkForDsh: false);

            var names = picked.Count > 0
                ? string.Join(", ", picked.Select(c => c.Title ?? c.Id.Entry))
                : "(空)";

            MainFile.Logger.Info($"[AI] FromHand pick: [{names}] for {selectionTag} (min={prefs.MinSelect} max={prefs.MaxSelect})");
            manager.AiLog.Log("AI_ACTION", $"手牌选牌: {names} | 启发式");

            RunManager.Instance.PlayerChoiceSynchronizer.SyncLocalChoice(
                player,
                choiceId,
                PlayerChoiceResult.FromMutableCombatCards(picked));

            return picked;
        }
        finally
        {
            await AiPlayerChoiceContextHelper.EndChoiceAsync(context, player);
        }
    }
}

// ---- Intercept FromHandForUpgrade for AI (e.g. Armaments) ----
[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Commands.CardSelectCmd), "FromHandForUpgrade")]
public static class FromHandForUpgradeAiPatch
{
    [HarmonyPrefix]
    public static bool Prefix(
        PlayerChoiceContext context,
        Player player,
        AbstractModel source,
        ref Task<CardModel?> __result)
    {
        var manager = AiTeammateManager.Instance;
        if (!manager.IsInitialized) return true;

        // dsh owns the local player: park a hand_select decision instead of opening the vanilla
        // UpgradeSelect UI, which dsh has no way to complete. 升级选牌改由 dsh 决策。
        if (DshHandSelect.AppliesTo(player))
        {
            __result = SelectUpgradeFromHandForDshAsync(context, player, source, manager);
            return false;
        }

        if (manager.AiPlayer == null) return true;
        if (player.NetId != manager.AiPlayer.NetId) return true;

        __result = SelectUpgradeFromHandForAiAsync(context, player, source, manager);
        return false;
    }

    private static async Task<CardModel?> SelectUpgradeFromHandForDshAsync(
        PlayerChoiceContext context,
        Player player,
        AbstractModel source,
        AiTeammateManager manager)
    {
        if (CombatManager.Instance.IsOverOrEnding)
            return null;

        NPlayerHand.Instance?.CancelAllCardPlay();

        var cards = PileType.Hand.GetPile(player).Cards
            .Where(c => c.IsUpgradable)
            .ToList();

        if (cards.Count <= 1)
            return cards.FirstOrDefault();

        var choiceId = RunManager.Instance.PlayerChoiceSynchronizer.ReserveChoiceId(player);
        await Compat.GameCompat.SignalPlayerChoiceBegun(context, player, PlayerChoiceOptions.CancelPlayCardActions);
        try
        {
            var sourceTag = source is CardModel sourceCard ? sourceCard.Id.Entry : "Upgrade";
            var picked = await DshHandSelect.AskAsync(
                player, cards, 1, 1, sourceTag, CancellationToken.None,
                "从中挑一张手牌升级", parkForDsh: true);
            var selected = picked.FirstOrDefault();
            var selectedName = selected?.Title ?? selected?.Id.Entry ?? "(空)";

            MainFile.Logger.Info($"[Agent] FromHandForUpgrade hand_select: [{selectedName}]");
            manager.AiLog.Log("AI_ACTION", $"手牌升级选择(dsh): {selectedName}");

            RunManager.Instance.PlayerChoiceSynchronizer.SyncLocalChoice(
                player,
                choiceId,
                PlayerChoiceResult.FromMutableCombatCard(selected));

            return selected;
        }
        finally
        {
            await AiPlayerChoiceContextHelper.EndChoiceAsync(context, player);
        }
    }

    private static async Task<CardModel?> SelectUpgradeFromHandForAiAsync(
        PlayerChoiceContext context,
        Player player,
        AbstractModel source,
        AiTeammateManager manager)
    {
        if (CombatManager.Instance.IsOverOrEnding)
            return null;

        NPlayerHand.Instance?.CancelAllCardPlay();

        var cards = PileType.Hand.GetPile(player).Cards
            .Where(c => c.IsUpgradable)
            .ToList();

        if (cards.Count <= 1)
            return cards.FirstOrDefault();

        var choiceId = RunManager.Instance.PlayerChoiceSynchronizer.ReserveChoiceId(player);
        await Compat.GameCompat.SignalPlayerChoiceBegun(context, player, PlayerChoiceOptions.CancelPlayCardActions);
        try
        {
            var picked = await DshHandSelect.AskAsync(
                player, cards, 1, 1, "Upgrade", CancellationToken.None,
                "从中挑一张手牌升级", parkForDsh: false);
            var selected = picked.FirstOrDefault();
            var selectedName = selected?.Title ?? selected?.Id.Entry ?? "(空)";

            MainFile.Logger.Info($"[AI] FromHandForUpgrade pick: [{selectedName}]");
            manager.AiLog.Log("AI_ACTION", $"手牌升级选择: {selectedName} | 启发式");

            RunManager.Instance.PlayerChoiceSynchronizer.SyncLocalChoice(
                player,
                choiceId,
                PlayerChoiceResult.FromMutableCombatCard(selected));

            return selected;
        }
        finally
        {
            await AiPlayerChoiceContextHelper.EndChoiceAsync(context, player);
        }
    }
}

// ---- dsh hand selection: replace the vanilla hand UI with a parked hand_select decision ----
/// <summary>
/// When dsh drives the local player, <c>NPlayerHand.SelectCards</c> would open a UI that dsh cannot
/// complete (and that soft-locks combat). Instead we park a <c>kind=hand_select</c> decision in
/// <see cref="AgentDecisionBroker"/> with the candidate cards as <c>options</c>; dsh answers with
/// <c>sts2_decide {"indices":[...]}</c> and the existing SyncLocalChoice path hands it back.
/// </summary>
internal static class DshHandSelect
{
    /// <summary>True when <paramref name="player"/> is the local player driven by dsh (not the mod's AI player).</summary>
    public static bool AppliesTo(Player player)
    {
        if (!AgentDecisionBroker.Enabled)
            return false;

        // Connected is not the same as driving. A bound dsh conversation that only sits in chat keeps
        // the socket open; taking the hand-selection UI away from the human then resolves the card with
        // a silent heuristic pick (or with zero cards) and no UI at all. Only take over while dsh keeps
        // calling the bridge. 连着 ≠ 在驱动：真人自己打牌时不能把选牌界面吞掉。
        if (!AgentDecisionBroker.AgentWatching)
            return false;

        var manager = AiTeammateManager.Instance;
        if (manager == null || !manager.IsInitialized)
            return false;

        // When the mod drives an AI player in this run, dsh is that AI's brain and the human local
        // player still uses the normal UI; only a plain local-player takeover needs this path.
        var runState = RunManager.Instance?.DebugOnlyGetState();
        if (manager.AiPlayer != null && runState != null && runState.Players.Contains(manager.AiPlayer))
            return false;

        return LocalContext.NetId is ulong localId && player.NetId == localId;
    }

    /// <summary>
    /// Resolves one hand selection with the game's real <c>MinSelect/MaxSelect</c> semantics (any count in
    /// the range may be confirmed, and 0 is legal when the minimum is 0 = "choose nothing and confirm").
    /// When dsh owns the choice — a local takeover, or dsh being the AI player's brain — this parks a
    /// <c>kind=hand_select</c> decision carrying that range plus every card's keyword / hover-tip sidebar
    /// text; otherwise, and whenever nobody answers, it falls back to the heuristic.
    /// 区间语义与词缀侧边栏都交给 dsh；没人应答才启发式兜底。
    /// </summary>
    public static async Task<List<CardModel>> AskAsync(
        Player player, List<CardModel> cards, int minSelect, int maxSelect,
        string selectionType, CancellationToken ct, string? header = null, bool parkForDsh = true)
    {
        if (cards.Count == 0)
            return new List<CardModel>();

        // Clamp the game's own range to what the hand actually offers. MaxSelect can be a sentinel
        // (Guards uses 999999999 = "any number"), so it never survives as a literal count.
        var min = Math.Clamp(minSelect, 0, cards.Count);
        var max = Math.Clamp(maxSelect <= 0 ? min : maxSelect, min, cards.Count);
        if (max <= 0)
            return new List<CardModel>();

        // No dsh behind this call (built-in heuristic path): pass the real range on, so "up to N" picks
        // up to N instead of collapsing to the minimum.
        //
        // Parking the AI player's own hand select for dsh is singleplayer-only on purpose: every peer
        // simulates the AI locally, and AgentDecisionBroker.Enabled is a per-machine flag, so letting it
        // decide which cards the AI takes would desync the peers' piles (same rule as the combat-plan
        // externalBrain flag). In multiplayer the heuristic answers on every peer — deterministically.
        // 多人局里 AI 的选牌必须各端一致，因此只有单人局才交给 dsh。
        if (!parkForDsh && !(AgentDecisionBroker.Enabled && SingleplayerRun()))
            return CardGridSelectionPatch.PickCardsByHeuristic(cards, min, max, selectionType);

        var options = new JsonArray();
        var prompt = new StringBuilder();
        prompt.AppendLine($"[手牌选择] 来源: {selectionType}");
        if (!string.IsNullOrWhiteSpace(header))
            prompt.AppendLine(header!);
        prompt.AppendLine(min == max
            ? $"从下面的手牌里选择正好 {min} 张（只能选列表里的牌）："
            : $"从下面的手牌里选择 {min}~{max} 张（只能选列表里的牌）：");
        prompt.AppendLine(min == 0
            ? "- 最少 0 张：可以一张都不选，返回 {\"indices\":[]} 等于「不选并确认」，效果按没选任何牌结算。"
            : $"- 最少 {min} 张，少于这个数游戏不接受。");
        if (max > min)
            prompt.AppendLine($"- 最多 {max} 张；只选 {min} 张也可以，你给出选择后由游戏确认（选够 {max} 张会自动确认）。");
        prompt.AppendLine("- 本次决策只处理这一次选牌；同一次出牌若连续多次要求选牌，会依次出现新的决策。");

        var query = AiTeammateManager.Instance?.QueryService;
        for (var i = 0; i < cards.Count; i++)
        {
            var card = cards[i];
            var current = PromptTextHelper.CleanText(
                PromptTextHelper.GetCardDescriptionText(card, PileType.Hand));
            // In-combat upgrade picks (Armaments, ...) are only worth making with the upgrade payoff
            // in view, so the prompt carries "current → upgraded" like the rest-site SMITH list.
            var preview = query?.FormatUpgradePreview(card, 80) ?? "";
            // Keywords + the hover-tip sidebar. Mod mechanics live there and never show up in the card
            // text, so without it dsh cannot judge modded cards. 词缀/新机制在悬停侧边栏里。
            var tips = PromptTextHelper.GetHoverTipText(card, 140, current);

            var line = $"{i}: {card.Title ?? card.Id.Entry} ({ContextBuilder.FormatCostCompact(card)})";
            if (!string.IsNullOrWhiteSpace(current))
                line += $" 当前: {PromptTextHelper.Truncate(current, 60)}";
            if (!string.IsNullOrWhiteSpace(preview))
                line += $" | {preview}";
            if (!string.IsNullOrWhiteSpace(tips))
                line += $" | 词缀: {tips}";
            prompt.AppendLine(line);

            var detail = string.IsNullOrWhiteSpace(preview)
                ? PromptTextHelper.Truncate(current, 80)
                : $"{PromptTextHelper.Truncate(current, 60)} | {preview}".Trim(' ', '|');
            if (!string.IsNullOrWhiteSpace(tips))
                detail = $"{detail} | 词缀: {tips}".Trim(' ', '|');

            options.Add(new JsonObject
            {
                ["index"] = i,
                ["card"] = card.Title ?? card.Id.Entry,
                ["upgradable"] = card.IsUpgradable,
                ["detail"] = PromptTextHelper.Truncate(detail, 240)
            });
        }
        prompt.AppendLine("请以 JSON 返回：{\"indices\":[...], \"reason\":\"理由\"}（indices 用上面的序号，可多选）");

        var systemPrompt = PersonalityPromptBuilder.BuildExternalAgentSystemPrompt(
            requireJson: true, includeTeamPrinciples: false);

        // The game's action queue is waiting on this choice: don't hold it for the broker's full
        // 300s budget when dsh is attached but idle.
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        // The range is machine-readable too: a prompt-only statement is easy for a model to miss.
        var extra = new JsonObject
        {
            ["min_select"] = min,
            ["max_select"] = max,
            ["allow_empty"] = min == 0,
            ["answer_shape"] = "{\"indices\":[...], \"reason\":\"...\"}"
        };

        var answer = await AgentDecisionBroker.RequestAsync(
            player, "hand_select", systemPrompt, prompt.ToString(), linkedCts.Token, options, extra);

        if (!TryParseIndices(answer, cards.Count, out var indices))
        {
            MainFile.Logger.Warn($"[Agent] hand_select not answered ({selectionType}); falling back to heuristic");
            return CardGridSelectionPatch.PickCardsByHeuristic(cards, min, max, selectionType);
        }

        var picked = indices.Select(index => cards[index]).ToList();
        if (picked.Count > max)
            picked = picked.Take(max).ToList();

        // Fewer than the game's minimum would be rejected by the game: top up to the minimum only.
        if (picked.Count < min)
        {
            foreach (var candidate in CardGridSelectionPatch.PickCardsByHeuristic(cards, min, min, selectionType))
            {
                if (picked.Contains(candidate)) continue;
                picked.Add(candidate);
                if (picked.Count >= min) break;
            }
        }

        if (picked.Count == 0 && min == 0)
            MainFile.Logger.Info($"[Agent] hand_select: dsh chose no card for {selectionType}");

        return picked;
    }

    /// <summary>True in a plain singleplayer run (no peers to keep in sync).</summary>
    private static bool SingleplayerRun()
    {
        try { return RunManager.Instance?.NetService?.Type == NetGameType.Singleplayer; }
        catch { return false; }
    }

    /// <summary>
    /// Parses {"indices":[...]} (or a single {"index":n}) into <paramref name="indices"/>. Returns false
    /// when there was no usable answer at all, so the caller can tell "dsh said choose nothing" (a valid
    /// answer when the minimum is 0) apart from "nobody answered".
    /// </summary>
    private static bool TryParseIndices(string? answer, int cardCount, out List<int> indices)
    {
        indices = new List<int>();
        if (string.IsNullOrWhiteSpace(answer))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(ResponseParser.ExtractJson(answer));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            if (root.TryGetProperty("indices", out var array) && array.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in array.EnumerateArray())
                {
                    var index = ReadIndex(element);
                    if (index.HasValue && index.Value >= 0 && index.Value < cardCount
                        && !indices.Contains(index.Value))
                        indices.Add(index.Value);
                }

                return true;
            }

            if (root.TryGetProperty("index", out var single))
            {
                var index = ReadIndex(single);
                if (index.HasValue && index.Value >= 0 && index.Value < cardCount)
                    indices.Add(index.Value);
                return true;
            }

            return false;
        }
        catch
        {
            // Malformed answer: the heuristic fallback takes over.
            return false;
        }
    }

    private static int? ReadIndex(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var number))
            return number;
        if (element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), out number))
            return number;
        return null;
    }
}

// ---- Choose-a-Card Selection (discard, Survivor effect, event card picks, etc.) ----
[HarmonyPatch(typeof(NChooseACardSelectionScreen), nameof(NChooseACardSelectionScreen.AfterOverlayShown))]
public static class ChooseACardSelectionPatch
{
    [HarmonyPostfix]
    public static void AfterOverlayShown(NChooseACardSelectionScreen __instance)
    {
        AutoPickForAi(__instance, "ChooseACard");
    }

    private static void AutoPickForAi(NChooseACardSelectionScreen screen, string source)
    {
        if (!GodotObject.IsInstanceValid(screen)) return;

        var manager = AiTeammateManager.Instance;
        if (!manager.IsInitialized || manager.AiPlayer == null) return;

        var holders = UiHelper.FindAll<NCardHolder>(screen)
            .Where(h => h.Visible && h.CardModel != null)
            .ToList();

        if (holders.Count == 0) return;

        var aiNetId = manager.AiPlayer.NetId;

        var aiHolders = holders
            .Where(h => h.CardModel?.Owner?.NetId == aiNetId)
            .ToList();

        var noOwnerHolders = holders
            .Where(h => h.CardModel?.Owner == null)
            .ToList();

        if (aiHolders.Count == 0 && noOwnerHolders.Count == holders.Count)
            aiHolders = holders;
        else if (aiHolders.Count == 0)
            return;

        // For in-combat (discard/Survivor/etc.) use fast heuristic.
        // Out-of-combat uses async LLM.
        var cards = aiHolders
            .Select(h => h.CardModel)
            .Where(c => c != null)
            .Cast<CardModel>()
            .ToList();

        if (CombatManager.Instance.IsInProgress)
        {
            var picked = CardGridSelectionPatch.PickCardsByHeuristic(cards, 1, 1, source);
            var name = picked[0].Title ?? picked[0].Id.Entry ?? "?";
            MainFile.Logger.Info($"AI heuristic pick: [{name}] for {source}");
            manager.AiLog.Log("AI_ACTION", $"智能选牌: {name} | 启发式选择");

            var tcsField = AccessTools.Field(typeof(NChooseACardSelectionScreen), "_completionSource");
            var tcs = tcsField?.GetValue(screen) as TaskCompletionSource<IEnumerable<CardModel>>;
            if (tcs != null && !tcs.Task.IsCompleted)
                tcs.TrySetResult(picked);

            // Defer removal to avoid re-entrancy during Push()
            Callable.From(() =>
            {
                try
                {
                    if (GodotObject.IsInstanceValid(screen))
                        MegaCrit.Sts2.Core.Nodes.Screens.Overlays.NOverlayStack.Instance?.Remove(screen);
                }
                catch { try { if (GodotObject.IsInstanceValid(screen)) screen.Hide(); } catch { } }
            }).CallDeferred();
        }
        else
        {
            _ = DoIntelligentSinglePickAsync(screen, manager, cards, source);
        }
    }

    private static async Task DoIntelligentSinglePickAsync(
        NChooseACardSelectionScreen screen, AiTeammateManager manager,
        List<CardModel> cards, string source)
    {

        var tcsField = AccessTools.Field(typeof(NChooseACardSelectionScreen), "_completionSource");
        var tcs = tcsField?.GetValue(screen) as TaskCompletionSource<IEnumerable<CardModel>>;

        // Hide screen so the human isn't blocked
        screen.Visible = false;
        try { MegaCrit.Sts2.Core.Nodes.Screens.Overlays.NOverlayStack.Instance?.HideBackstop(); } catch { }

        CardModel picked;
        string reason;
        try
        {
            var (indices, r) = await manager.DecisionService.DecideCardSelectionAsync(
                manager.AiPlayer!, cards, 1, 1, source, CancellationToken.None);
            reason = r;
            picked = indices.Count > 0 && indices[0] < cards.Count ? cards[indices[0]] : cards[0];
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"AI intelligent single pick failed: {ex.Message}, falling back to heuristic");
            reason = "AI调用失败，启发式选择";
            picked = CardGridSelectionPatch.PickCardsByHeuristic(cards, 1, 1, source)[0];
        }

        var name = picked.Title ?? picked.Id.Entry ?? "?";
        MainFile.Logger.Info($"AI intelligent pick: [{name}] for {source}: {reason}");
        if (!string.IsNullOrWhiteSpace(reason))
            manager.AiLog.Log("AI_THINK", $"[{source}] {reason}");
        manager.AiLog.Log("AI_ACTION", $"智能选牌: {name} | {reason}");

        tcs?.TrySetResult(new[] { picked });

        if (GodotObject.IsInstanceValid(screen))
        {
            var s = screen;
            Callable.From(() =>
            {
                try { MegaCrit.Sts2.Core.Nodes.Screens.Overlays.NOverlayStack.Instance?.Remove(s); }
                catch { try { s.QueueFree(); } catch { } }
            }).CallDeferred();
        }
    }
}

// ---- In-Combat Card Grid Selection ----
[HarmonyPatch(typeof(NCardGridSelectionScreen), nameof(NCardGridSelectionScreen.AfterOverlayShown))]
public static class CardGridSelectionPatch
{
    [HarmonyPostfix]
    public static void AfterOverlayShown(NCardGridSelectionScreen __instance)
    {
        AutoPickGridForAi(__instance);
    }

    internal static void AutoPickGridForAi(NCardGridSelectionScreen screen)
    {
        if (!GodotObject.IsInstanceValid(screen)) return;

        var manager = AiTeammateManager.Instance;
        if (!manager.IsInitialized || manager.AiPlayer == null) return;

        var holders = UiHelper.FindAll<NCardHolder>(screen)
            .Where(h => h.Visible && h.CardModel != null)
            .ToList();

        if (holders.Count == 0) return;

        var aiNetId = manager.AiPlayer.NetId;
        var aiHolders = holders
            .Where(h => h.CardModel?.Owner?.NetId == aiNetId)
            .ToList();

        if (aiHolders.Count == 0)
        {
            var noOwnerCount = holders.Count(h => h.CardModel?.Owner == null);
            if (noOwnerCount == holders.Count)
                aiHolders = holders;
            else
                return;
        }

        // Read _prefs to determine how many cards to select (MinSelect / MaxSelect)
        int minSelect = 1;
        int maxSelect = 1;
        try
        {
            var prefsField = AccessTools.Field(screen.GetType(), "_prefs");
            if (prefsField != null)
            {
                var prefs = prefsField.GetValue(screen);
                if (prefs != null)
                {
                    minSelect = (int)(AccessTools.Property(prefs.GetType(), "MinSelect")?.GetValue(prefs) ?? 1);
                    maxSelect = (int)(AccessTools.Property(prefs.GetType(), "MaxSelect")?.GetValue(prefs) ?? minSelect);
                }
            }
        }
        catch { /* use defaults */ }

        int toPick = Math.Min(maxSelect, aiHolders.Count);
        if (toPick < minSelect) toPick = Math.Min(minSelect, aiHolders.Count);

        MainFile.Logger.Info($"AI auto-picking {toPick} card(s) in {screen.GetType().Name} (min={minSelect} max={maxSelect}, {aiHolders.Count} eligible)");

        // For in-combat card selections (Cleanse, enchant, transform, discard, etc.)
        // use a fast heuristic — LLM calls take 1-8s and would stall the AI's turn.
        // Out-of-combat selections (event card picks) go through the LLM path.
        var cards = aiHolders
            .Select(h => h.CardModel)
            .Where(c => c != null)
            .Cast<CardModel>()
            .ToList();

        bool isInCombat = CombatManager.Instance.IsInProgress;
        var screenTypeName = screen.GetType().Name;
        bool isUpgradeScreen = screenTypeName.Contains("Upgrade", StringComparison.OrdinalIgnoreCase);

        // For combat OR upgrade screens (rest site SMITH): use fast heuristic.
        // Upgrade screens open for AI during rest sites and must be dismissed
        // immediately — otherwise NOverlayStack blocks human player input.
        if (isInCombat || isUpgradeScreen)
        {
            // Fast local heuristic
            var picked = PickCardsByHeuristic(cards, minSelect, maxSelect, screenTypeName);
            var names = string.Join(", ", picked.Select(c => c.Title ?? c.Id.Entry));
            MainFile.Logger.Info($"AI heuristic pick: [{names}] for {screenTypeName}");
            manager.AiLog.Log("AI_ACTION", $"智能选牌: {names} ({picked.Count}张) | 启发式选择");

            // Complete TCS synchronously so the game's await on CardsSelected() continues
            var tcsField = AccessTools.Field(typeof(NCardGridSelectionScreen), "_completionSource");
            var tcs = tcsField?.GetValue(screen) as TaskCompletionSource<IEnumerable<CardModel>>;
            if (tcs != null && !tcs.Task.IsCompleted)
                tcs.TrySetResult(picked);

            // CRITICAL: Do NOT call NOverlayStack.Remove() synchronously here.
            // AfterOverlayShown is called DURING Push(), and calling Remove()
            // re-entrantly corrupts the overlay stack state, leaving the backstop
            // (black overlay) visible and blocking mouse input.
            // Instead, defer the removal to the next frame after Push() completes.
            Callable.From(() =>
            {
                try
                {
                    if (GodotObject.IsInstanceValid(screen))
                        MegaCrit.Sts2.Core.Nodes.Screens.Overlays.NOverlayStack.Instance?.Remove(screen);
                }
                catch
                {
                    try { if (GodotObject.IsInstanceValid(screen)) screen.Hide(); } catch { }
                }
            }).CallDeferred();
        }
        else
        {
            // Out-of-combat: use async LLM for smarter picks
            _ = DoIntelligentGridPickAsync(screen, manager, cards, minSelect, maxSelect);
        }
    }

    /// <summary>
    /// Fast heuristic for in-combat card selection (Cleanse, enchant, transform, etc.).
    /// Prioritizes removing/selecting weak cards over strong ones.
    /// </summary>
    internal static List<CardModel> PickCardsByHeuristic(
        List<CardModel> cards, int minSelect, int maxSelect, string screenType)
    {
        int toPick = Math.Min(maxSelect, cards.Count);
        if (toPick < minSelect) toPick = minSelect;

        // Determine selection context from screen class name
        bool isRemoval = screenType.Contains("DeckCardSelect") || screenType.Contains("Remove");
        bool isEnchant = screenType.Contains("Enchant");
        bool isUpgrade = screenType.Contains("Upgrade");
        bool isTransform = screenType.Contains("Transform");

        // Score each card (higher score = more likely to be picked for removal,
        // lower score = more likely to be kept / picked for upgrade/enchant)
        var scored = cards.Select(c =>
        {
            int score = 0;
            var id = c.Id.Entry.ToLowerInvariant();
            var title = (c.Title ?? "").ToLowerInvariant();
            var rarity = c.Rarity.ToString().ToLowerInvariant();

            // Curses and status cards: always best to remove/worst to keep
            if (rarity.Contains("curse") || rarity.Contains("basic"))
                score += 100;
            // Basic Strike/Defend variants
            if (title.Contains("strike") || title.Contains("打击"))
                score += 50;
            if (title.Contains("defend") || title.Contains("防御"))
                score += 40;
            // Common cards: decent removal candidates
            if (rarity.Contains("common"))
                score += 20;
            // Rare cards: bad to remove
            if (rarity.Contains("rare"))
                score -= 50;
            // Upgraded cards: bad to remove
            if (id.Contains("plus") || title.Contains("+"))
                score -= 60;
            // High-cost cards in a low-energy deck: might be good to remove
            // (heuristic only, no energy context available here)

            return (card: c, score);
        }).ToList();

        if (isRemoval)
        {
            // For removal: pick highest-score cards (worst cards)
            return scored.OrderByDescending(x => x.score).Take(toPick).Select(x => x.card).ToList();
        }
        else if (isEnchant || isUpgrade)
        {
            // For enchant/upgrade: pick lowest-score cards (best cards to improve)
            // But avoid already-upgraded cards
            var candidates = scored
                .Where(x => !x.card.Id.Entry.ToLowerInvariant().Contains("plus"))
                .ToList();
            if (candidates.Count == 0) candidates = scored;
            return candidates.OrderBy(x => x.score).Take(toPick).Select(x => x.card).ToList();
        }
        else
        {
            // For transform / unknown: pick mid-score cards (basic/mediocre)
            return scored.OrderByDescending(x => x.score).Take(toPick).Select(x => x.card).ToList();
        }
    }

    internal static string DescribeHandSelectionSource(AbstractModel? source, CardSelectorPrefs prefs, string fallback)
    {
        if (source is CardModel card)
            return card.Id.Entry;

        var typeName = source?.GetType().Name;
        if (!string.IsNullOrWhiteSpace(typeName))
            return typeName;

        var promptText = prefs.Prompt.GetFormattedText();
        return string.IsNullOrWhiteSpace(promptText) ? fallback : promptText;
    }

    /// <summary>
    /// Out-of-combat async LLM-based card selection (events, rest sites, etc.).
    /// Not used during combat — PickCardsByHeuristic is used there instead.
    /// </summary>
    private static async Task DoIntelligentGridPickAsync(
        NCardGridSelectionScreen screen, AiTeammateManager manager,
        List<CardModel> cards, int minSelect, int maxSelect)
    {
        var screenType = screen.GetType().Name;

        var tcsField = AccessTools.Field(typeof(NCardGridSelectionScreen), "_completionSource");
        var tcs = tcsField?.GetValue(screen) as TaskCompletionSource<IEnumerable<CardModel>>;

        // Hide screen so the human isn't blocked while LLM thinks
        screen.Visible = false;
        try { MegaCrit.Sts2.Core.Nodes.Screens.Overlays.NOverlayStack.Instance?.HideBackstop(); } catch { }

        List<CardModel> pickedCards;
        string reason;
        try
        {
            var (indices, r) = await manager.DecisionService.DecideCardSelectionAsync(
                manager.AiPlayer!, cards, minSelect, maxSelect, screenType,
                CancellationToken.None);
            reason = r;
            pickedCards = indices.Select(i => cards[i]).ToList();
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"AI intelligent grid pick failed: {ex.Message}, falling back to heuristic");
            reason = "AI调用失败，启发式选择";
            pickedCards = PickCardsByHeuristic(cards, minSelect, maxSelect, screenType);
        }

        var names = string.Join(", ", pickedCards.Select(c => c.Title ?? c.Id.Entry));
        MainFile.Logger.Info($"AI intelligent pick: [{names}] for {screenType}: {reason}");
        if (!string.IsNullOrWhiteSpace(reason))
            manager.AiLog.Log("AI_THINK", $"[{screenType}] {reason}");
        manager.AiLog.Log("AI_ACTION", $"智能选牌: {names} ({pickedCards.Count}张) | {reason}");

        // Complete TCS (thread-safe) and schedule screen cleanup on main thread
        tcs?.TrySetResult(pickedCards);

        if (GodotObject.IsInstanceValid(screen))
        {
            var s = screen;
            Callable.From(() =>
            {
                try { MegaCrit.Sts2.Core.Nodes.Screens.Overlays.NOverlayStack.Instance?.Remove(s); }
                catch { try { s.QueueFree(); } catch { } }
            }).CallDeferred();
        }
    }
}

// ---- In-Combat Deck Card Selection (e.g. Cleanse, card removal effects) ----
// NDeckCardSelectScreen overrides AfterOverlayShown(), so the base-class patch
// on NCardGridSelectionScreen doesn't fire for it. We need a separate patch.
[HarmonyPatch(typeof(NDeckCardSelectScreen), nameof(NDeckCardSelectScreen.AfterOverlayShown))]
public static class DeckCardSelectPatch
{
    [HarmonyPostfix]
    public static void AfterOverlayShown(NDeckCardSelectScreen __instance)
    {
        CardGridSelectionPatch.AutoPickGridForAi(__instance);
    }
}

// NOTE: the old PeerInputSynchronizer.ForceGetStateForPlayer patch was removed. That method no
// longer exists in 0.107.1 or 0.111.0; GetScreenType now goes through GetOrCreateStateForPlayer,
// so AI players no longer throw when hand visibility refreshes.

// ---- Auto-reconnect AI player when loading a saved multiplayer run ----
// When the host exits and re-enters a game, the LoadRunLobby waits for all
// players from the save to reconnect via Steam. The AI has no network peer
// so it can never send a ClientLoadJoinRequestMessage → never appears in
// ConnectedPlayerIds → warning popup every time and potential game break.
// This patch adds the AI to ConnectedPlayerIds immediately on host init,
// before any clients connect, so the AI is always "already connected".
[HarmonyPatch(typeof(NMultiplayerLoadGameScreen), "InitializeAsHost")]
public static class LoadGameScreenHostPatch
{
    [HarmonyPostfix]
    public static void AfterInitializeAsHost(NMultiplayerLoadGameScreen __instance)
    {
        var manager = AiTeammateManager.Instance;
        if (!manager.IsInitialized) return;

        try
        {
            var lobbyField = AccessTools.Field(typeof(NMultiplayerLoadGameScreen), "_runLobby");
            var lobby = lobbyField?.GetValue(__instance) as LoadRunLobby;
            if (lobby == null) return;

            var players = lobby.Run?.Players;
            if (players == null) return;

            foreach (var p in players)
            {
                if (AiIdentity.IsAi(p.NetId))
                {
                    Compat.GameCompat.MarkLoadLobbyPlayerConnected(lobby, p.NetId);
                    MainFile.Logger.Info($"[LOAD] AI player {p.NetId} ({p.CharacterId}) auto-reconnected to loaded lobby");
                }
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[LOAD] Failed to auto-reconnect AI: {ex.Message}");
        }
    }
}

// ---- Re-detect AI player after loading a saved multiplayer run ----
[HarmonyPatch(typeof(NGame), nameof(NGame.LoadRun))]
public static class LoadRunPatch
{
    [HarmonyPostfix]
    public static void AfterLoadRun()
    {
        var manager = AiTeammateManager.Instance;
        if (!manager.TryInitialize()) return;

        // A loaded run is a brand-new RunState with brand-new Player objects; RunManager.State already
        // points at it here (SetUpSaved* runs before LoadRun). Re-run detection so the stale cached
        // reference is replaced — otherwise events and map votes throw IndexOutOfRange
        // (GetPlayerSlotIndex compares by reference) and the AI sits frozen after SL.
        manager.DetectAndBindAiPlayer();
        if (manager.AiPlayer != null)
            MainFile.Logger.Info($"AI player re-detected after save load: {manager.AiPlayer.Character?.Id.Entry}");

        // The postfix runs before the async load finishes, so the local net id may not exist yet.
        // Retry in the background; NRun._Ready usually wins the race.
        if (!SoloRunController.IsActive)
            SoloRunController.RequestBindRetry("LoadRun");
    }
}

// ---- Auto-pick relic for AI in treasure rooms ----
// BeginRelicPicking runs when the room is entered — long before the relic collection screen exists.
// Picking there awarded the relic while NTreasureRoomRelicCollection._holdersInUse was still empty,
// so AnimateRelicAwards threw "Sequence contains no matching element" and the chest never finished
// opening. Vote once the local collection has been initialized instead, and route the vote through
// the action queue so multiplayer peers apply it too (an OnPicked call stays local to this machine).
//
// The pick itself is an AI decision now: one relic is taken as-is, several relics go through
// DecisionService.DecideTreasureRelicAsync (LLM backend, or the parked dsh decision when it drives
// the AI) and TreasureRelicHeuristic is the local fallback. It used to be `Random.Shared.Next`.
[HarmonyPatch(typeof(NTreasureRoomRelicCollection), nameof(NTreasureRoomRelicCollection.InitializeRelics))]
public static class TreasureRoomRelicAutoPickPatch
{
    /// <summary>
    /// Cap for the built-in backend so a hung request cannot freeze the opened chest. With dsh as the
    /// brain there is no extra cap — the broker owns the wait budget and its agent thinks longer.
    /// </summary>
    private const int DecisionDeadlineMs = 30000;

    [HarmonyPostfix]
    public static void AfterInitializeRelics()
    {
        var manager = AiTeammateManager.Instance;
        if (!manager.IsInitialized || manager.AiPlayer == null) return;

        // The AI player is simulated by the host; clients must not vote on its behalf.
        var run = RunManager.Instance;
        var netType = run?.NetService?.Type;
        if (netType is not (NetGameType.Singleplayer or NetGameType.Host)) return;

        var sync = run!.TreasureRoomRelicSynchronizer;
        var relics = sync?.CurrentRelics;
        if (relics == null || relics.Count == 0) return;

        var aiPlayer = manager.AiPlayer!;

        // Already voted: singleplayer-with-fake-players pre-votes for non-local players, and a
        // second InitializeRelics for the same chest must not vote twice.
        if (HasVoted(sync!, aiPlayer)) return;

        // One relic (singleplayer, solo climb): nothing to decide, vote at once so the chest
        // finishes without a model round trip.
        if (relics.Count == 1)
        {
            Vote(manager, run, sync!, aiPlayer, 0, relics[0], 1, "");
            return;
        }

        // Several relics: let the AI choose. Fire and forget — the chest waits for this vote and the
        // request has to run out-of-band, but the game must not be held on this stack frame.
        var candidates = relics.ToList();
        _ = DecideAndVoteAsync(manager, run, sync!, aiPlayer, candidates);
    }

    private static bool HasVoted(TreasureRoomRelicSynchronizer sync, Player aiPlayer)
    {
        try
        {
            var vote = sync.GetPlayerVote(aiPlayer);
            return vote is { voteReceived: true };
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Teammates that already voted, for the decision prompt (contention/RPS awareness).</summary>
    private static string? BuildTeammateVoteNote(
        RunManager run, TreasureRoomRelicSynchronizer sync, Player aiPlayer)
    {
        try
        {
            var players = run.DebugOnlyGetState()?.Players;
            if (players == null) return null;

            var notes = new List<string>();
            foreach (var player in players)
            {
                if (player == aiPlayer) continue;

                var vote = sync.GetPlayerVote(player);
                if (vote is not { voteReceived: true }) continue;

                var name = player.Character?.Id.Entry ?? "队友";
                notes.Add(vote.index is { } index ? $"{name} 已投 遗物#{index}" : $"{name} 已跳过");
            }

            return notes.Count == 0 ? null : $"队友投票: {string.Join("；", notes)}";
        }
        catch
        {
            return null;
        }
    }

    private static async Task DecideAndVoteAsync(
        AiTeammateManager manager,
        RunManager run,
        TreasureRoomRelicSynchronizer sync,
        Player aiPlayer,
        List<RelicModel> relics)
    {
        var count = relics.Count;
        int index;
        string reason;

        try
        {
            // Solo climb: the human may have interrupted the AI (⏸ / chat). Hold like every other
            // solo flow does; the chest simply waits for the resumed vote.
            if (SoloRunController.IsActive)
                await AiFlowControl.WaitWhilePausedAsync();

            var note = BuildTeammateVoteNote(run, sync, aiPlayer);
            var (choice, why) = await DecideWithDeadlineAsync(manager, aiPlayer, relics, note);

            if (choice >= 0 && choice < count)
            {
                index = choice;
                reason = why;
            }
            else
            {
                index = TreasureRelicHeuristic.Pick(aiPlayer, relics);
                reason = string.IsNullOrWhiteSpace(why) ? "启发式兜底" : $"启发式兜底（{why}）";
            }
        }
        catch (OperationCanceledException)
        {
            index = TreasureRelicHeuristic.Pick(aiPlayer, relics);
            reason = "单人爬塔已暂停，启发式兜底";
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[TreasureRelic] AI 遗物决策失败: {ex.Message}");
            index = TreasureRelicHeuristic.Pick(aiPlayer, relics);
            reason = "决策失败，启发式兜底";
        }

        if (index < 0 || index >= count)
            index = 0;

        try
        {
            // The chest may have resolved while we were asking (other vote / room left / run ended);
            // a different chest could also have opened in the meantime, so match the candidates too.
            MainThread.Run(() =>
            {
                var current = sync.CurrentRelics;
                if (current == null || current.Count != count) return;
                for (var i = 0; i < count; i++)
                {
                    if (!ReferenceEquals(current[i], relics[i])) return;
                }
                if (HasVoted(sync, aiPlayer)) return;
                Vote(manager, run, sync, aiPlayer, index, relics[index], count, reason);
            });
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[TreasureRelic] 投票失败: {ex.Message}");
        }
    }

    private static async Task<(int choice, string reason)> DecideWithDeadlineAsync(
        AiTeammateManager manager,
        Player aiPlayer,
        List<RelicModel> relics,
        string? teammateVoteNote)
    {
        var task = manager.DecisionService.DecideTreasureRelicAsync(
            aiPlayer, relics, teammateVoteNote, CancellationToken.None);

        if (AgentDecisionBroker.Enabled)
            return await task;

        var finished = await Task.WhenAny(task, Task.Delay(DecisionDeadlineMs));
        if (finished == task)
            return await task;

        // The backend ignored its cancellation token; keep the fault observed.
        _ = task.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        MainFile.Logger.Warn($"[TreasureRelic] AI 决策超过 {DecisionDeadlineMs}ms，改用启发式");
        return (-1, "决策超时");
    }

    private static void Vote(
        AiTeammateManager manager,
        RunManager run,
        TreasureRoomRelicSynchronizer sync,
        Player aiPlayer,
        int index,
        RelicModel? relic,
        int count,
        string reason)
    {
        var name = relic?.Id.Entry ?? "?";
        MainFile.Logger.Info($"[TreasureRelic] AI 选择遗物 {index}/{count}: {name} | {reason}");
        manager.AiLog.Log("AI_ACTION", $"选择遗物: {name} ({count}选1, AI)");
        if (!string.IsNullOrWhiteSpace(reason))
            manager.AiLog.Log("AI_THINK", $"[宝箱遗物] {reason}");

        try
        {
            if (LocalContext.NetId.HasValue && LocalContext.NetId.Value == aiPlayer.NetId)
            {
                // Solo climb: the AI owns the local player, so use the local-player path.
                sync.PickRelicLocally(index);
            }
            else
            {
                // Multiplayer teammate: broadcast the AI's vote so every peer counts it.
                run.ActionQueueSynchronizer.RequestEnqueue(new PickRelicAction(aiPlayer, index));
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"AI relic pick failed: {ex.Message}");
        }
    }
}
