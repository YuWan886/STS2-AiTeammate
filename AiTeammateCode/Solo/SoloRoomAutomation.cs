using MegaCrit.Sts2.Core.AutoSlay.Helpers;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.RestSite;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using AiTeammate.AiTeammateCode.Compat;
using AiTeammate.AiTeammateCode.Decision;

namespace AiTeammate.AiTeammateCode.Solo;

/// <summary>
/// Solo-mode room automation. Unlike multiplayer teammate mode (where the human drives the room and
/// the AI only fills in its own choices), in solo the AI is the local player, so these flows call
/// the same APIs the human UI calls.
/// </summary>
public static class SoloRoomAutomation
{
    // ---- Rest site ------------------------------------------------------------------

    public static void HandleRestSite(RestSiteSynchronizer sync, Player aiPlayer, AiTeammateManager manager, int pickCount = 0)
    {
        IReadOnlyList<MegaCrit.Sts2.Core.Entities.RestSite.RestSiteOption> options;
        int currentHp;
        int maxHp;
        try
        {
            options = sync.GetOptionsForPlayer(aiPlayer);
            currentHp = aiPlayer.Creature?.CurrentHp ?? 0;
            maxHp = aiPlayer.Creature?.MaxHp ?? 1;
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Solo] rest site options failed: {ex.Message}");
            return;
        }

        if (options == null || options.Count == 0)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1200);
                await AiFlowControl.WaitWhilePausedAsync();

                RestSiteDecisionResult decision;
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    decision = await manager.DecisionService.DecideRestSiteAsync(
                        aiPlayer, currentHp, maxHp, options, cts.Token);
                }
                catch (Exception ex)
                {
                    MainFile.Logger.Warn($"[Solo] rest decision failed: {ex.Message}");
                    decision = new RestSiteDecisionResult { Choice = PickRestFallback(options, currentHp, maxHp) };
                }

                var pick = decision.Choice;
                if (pick < 0 || pick >= options.Count)
                    pick = PickRestFallback(options, currentHp, maxHp);

                if (options[pick].OptionId == "SMITH" && decision.UpgradeIndices.Count > 0)
                    manager.SetPendingRestSiteUpgradeIndices(decision.UpgradeIndices);
                else
                    manager.SetPendingRestSiteUpgradeIndices(null);

                var reason = string.IsNullOrWhiteSpace(decision.Reason) ? "本地兜底" : decision.Reason;
                var optionId = options[pick].OptionId;
                manager.AiLog.Log("AI_THINK", $"[休息站] {reason}");
                manager.AiLog.Log("AI_ACTION", $"休息站选择: {optionId} | {reason}");

                await AiFlowControl.WaitWhilePausedAsync();
                var chosenOption = options[pick];
                MainThread.Post(() => _ = ChooseRestOptionAsync(sync, aiPlayer, pick, chosenOption, manager, pickCount));
            }
            catch (Exception ex)
            {
                MainFile.Logger.Warn($"[Solo] rest site handler failed: {ex.Message}");
            }
        });
    }

    private static async Task ChooseRestOptionAsync(RestSiteSynchronizer sync, Player aiPlayer, int index,
        RestSiteOption option, AiTeammateManager manager, int pickCount)
    {
        try
        {
            // Click the real button when possible: the UI flow also disables the other options,
            // plays the VFX and shows the proceed button once the choice resolves.
            var room = NRestSiteRoom.Instance;
            var button = room != null && Godot.GodotObject.IsInstanceValid(room)
                ? room.GetButtonForOption(option)
                : null;

            if (button != null && Godot.GodotObject.IsInstanceValid(button))
            {
                await UiHelper.Click(button);
                MainFile.Logger.Info($"[Solo] rest option clicked: {option.OptionId}");
            }
            else
            {
                var ok = await sync.ChooseLocalOption(index);
                MainFile.Logger.Info($"[Solo] rest option chosen: {option.OptionId}");

                // Mirrors NRestSiteButton.SelectOption: hide the choices and reveal proceed.
                var restRoom = NRestSiteRoom.Instance;
                if (ok && restRoom != null && Godot.GodotObject.IsInstanceValid(restRoom))
                    restRoom.AfterSelectingOption(option);
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Solo] rest choice failed: {ex.Message}");
            manager.AiLog.LogError("休息站", ex.Message);
        }

        // Some relics allow picking more than one rest-site option; the game re-enables the
        // buttons in that case. Pick again (bounded) instead of leaving the room waiting.
        if (pickCount >= 2)
            return;

        try
        {
            await Task.Delay(900);
            await AiFlowControl.WaitWhilePausedAsync();

            var room = NRestSiteRoom.Instance;
            if (room == null || !Godot.GodotObject.IsInstanceValid(room))
                return;

            var stillSelectable = UiHelper.FindAll<NRestSiteButton>(room)
                .Any(b => Godot.GodotObject.IsInstanceValid(b) && b.IsEnabled && b.IsVisibleInTree());
            if (!stillSelectable)
                return;

            MainFile.Logger.Info("[Solo] rest site allows another pick, deciding again");
            HandleRestSite(sync, aiPlayer, manager, pickCount + 1);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Solo] rest site follow-up failed: {ex.Message}");
        }
    }

    private static int PickRestFallback(IReadOnlyList<MegaCrit.Sts2.Core.Entities.RestSite.RestSiteOption> options,
        int currentHp, int maxHp)
    {
        var hpPct = maxHp > 0 ? currentHp * 100 / maxHp : 100;
        var heal = options.ToList().FindIndex(o => o.OptionId == "HEAL" && o.IsEnabled);
        if (heal < 0) heal = options.ToList().FindIndex(o => o.OptionId == "MEND" && o.IsEnabled);
        var smith = options.ToList().FindIndex(o => o.OptionId == "SMITH" && o.IsEnabled);

        if (hpPct <= 55 && heal >= 0)
            return heal;
        if (smith >= 0)
            return smith;
        if (heal >= 0)
            return heal;

        for (var i = 0; i < options.Count; i++)
        {
            if (options[i].IsEnabled)
                return i;
        }

        return 0;
    }

    // ---- Rewards --------------------------------------------------------------------

    /// <summary>
    /// Solo mode suppresses the native rewards screen, so the AI has to perform the screen's
    /// "proceed" step itself (open the map, or signal ready for the next act after a boss).
    /// </summary>
    public static async Task ProceedAfterRoomRewardsAsync(AiTeammateManager manager)
    {
        try
        {
            var run = RunManager.Instance;
            var state = run?.DebugOnlyGetState();
            if (run == null || state == null)
                return;

            var room = state.CurrentRoom;
            if (room == null)
                return;

            if (room.RoomType == RoomType.Boss || room.IsVictoryRoom)
            {
                if (state.Map.SecondBossMapPoint != null
                    && state.CurrentMapCoord == state.Map.BossMapPoint.coord)
                {
                    await run.ProceedFromTerminalRewardsScreen();
                }
                else
                {
                    run.ActChangeSynchronizer.SetLocalPlayerReady();
                }

                manager.AiLog.Log("AI_ACTION", "Boss 奖励领取完成，准备进入下一幕");
                return;
            }

            await run.ProceedFromTerminalRewardsScreen();
            manager.AiLog.Log("AI_ACTION", "奖励领取完成，返回地图");
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Solo] proceed after rewards failed: {ex.Message}");
        }
    }

    // ---- Shop -----------------------------------------------------------------------

    public static void HandleShop(AiTeammateManager manager, Player aiPlayer)
    {
        _ = RunShopAsync(manager, aiPlayer);
    }

    private static async Task RunShopAsync(AiTeammateManager manager, Player aiPlayer)
    {
        try
        {
            await Task.Delay(900);
            await AiFlowControl.WaitWhilePausedAsync();

            NMerchantRoom? room = null;
            for (var attempt = 0; attempt < 16; attempt++)
            {
                room = NMerchantRoom.Instance;
                if (room != null && Godot.GodotObject.IsInstanceValid(room))
                    break;
                room = null;
                await Task.Delay(500);
            }

            if (room == null)
            {
                MainFile.Logger.Warn("[Solo] merchant room not ready");
                return;
            }

            room.OpenInventory();
            await Task.Delay(700);
            await AiFlowControl.WaitWhilePausedAsync();

            var inventoryNode = room.Inventory;
            var inventory = inventoryNode?.Inventory;
            if (inventoryNode == null || inventory == null)
            {
                MainFile.Logger.Warn("[Solo] merchant inventory unavailable");
                return;
            }

            // Capture the entry models up front: the inventory slot nodes may be rebuilt/freed
            // as items are purchased, but the entries stay valid for purchases.
            var affordable = inventoryNode.GetAllSlots()
                .Select(slot => slot.Entry)
                .Where(entry => entry.IsStocked && entry.EnoughGold)
                .ToList();

            if (affordable.Count == 0)
            {
                manager.AiLog.Log("SHOP", "没有买得起的商品，离开商店");
                await CloseInventoryAsync(room);
                return;
            }

            var entries = affordable.Select(OutOfCombatExecutor.CreateShopDecisionEntry).ToList();
            manager.AiLog.Log("SHOP", $"{affordable.Count} 件可购买商品 | Gold={aiPlayer.Gold}");

            var (choices, reason) = await manager.DecisionService.DecideShopAsync(
                aiPlayer, entries, CancellationToken.None);

            if (!string.IsNullOrWhiteSpace(reason))
                manager.AiLog.Log("AI_THINK", $"[商店] {reason}");

            var valid = choices
                .Where(i => i >= 0 && i < affordable.Count)
                .Distinct()
                .OrderBy(i => i)
                .ToList();

            if (valid.Count == 0)
            {
                manager.AiLog.Log("AI_ACTION", "离开商店");
                await CloseInventoryAsync(room);
                return;
            }

            foreach (var index in valid)
            {
                await AiFlowControl.WaitWhilePausedAsync();

                var entry = affordable[index];
                if (!entry.IsStocked || !entry.EnoughGold)
                    continue;

                manager.AiLog.Log("AI_ACTION", $"购买: {entries[index].DisplayText}");
                bool success;
                try
                {
                    // 删牌走 AI 自己的牌堆与金币。单人是 AI 自己当本地玩家，走原路也不会错，
                    // 但保持和多人同一条正确路径，避免任何情况下误删真人的牌。
                    success = entry is MerchantCardRemovalEntry removalEntry
                        ? await AiCardShopRemoval.TryBuyAsync(aiPlayer, removalEntry, msg => manager.AiLog.Log("SHOP", msg))
                        : await entry.OnTryPurchaseWrapper(inventory);
                }
                catch (Exception ex)
                {
                    manager.AiLog.LogError("SHOP", $"购买失败: {ex.Message}");
                    break;
                }

                manager.AiLog.Log("SHOP", $"购买结果: {(success ? "成功" : "失败")} | Gold={aiPlayer.Gold}");
                if (!success)
                    break;

                // 删牌槽位是 UI 手动刷新的（游戏原流程在 DoMerchantCardRemoval 里做），这里补上。
                if (entry is MerchantCardRemovalEntry)
                {
                    try { room.Inventory.OnCardRemovalUsed(); } catch { }
                }

                await Task.Delay(250);
            }

            await CloseInventoryAsync(room);
            manager.AiLog.Log("SHOP", "商店购物结束");
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Solo] shop handler failed: {ex.Message}");
            manager.AiLog.LogError("SHOP", ex.Message);
        }
    }

    private static async Task CloseInventoryAsync(NMerchantRoom room)
    {
        try
        {
            if (room == null || !Godot.GodotObject.IsInstanceValid(room))
                return;

            var back = UiHelper.FindFirst<NBackButton>(room);
            if (back != null && Godot.GodotObject.IsInstanceValid(back))
                await UiHelper.Click(back);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Solo] closing merchant inventory failed: {ex.Message}");
        }
    }
}
