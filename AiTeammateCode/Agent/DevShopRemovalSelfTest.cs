using System.Text.Json.Nodes;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Runs;
using AiTeammate.AiTeammateCode.Compat;
using AiTeammate.AiTeammateCode.Decision;

namespace AiTeammate.AiTeammateCode.Agent;

/// <summary>
/// 测试用动作 <c>sts2_test_shop_removal</c>：在一局进行中的对局里，替 AI 直接走一遍商店「删牌」，
/// 把「AI 扣钱/掉牌、真人金币与牌组不变、没有给真人弹选牌界面」这些断言打成 JSON 返回，
/// 供 tools/test-shop-removal.ps1 做无人工回归。
/// 会消耗 AI 的商店随机数并真的改对局数据，请只在一次性的测试局里跑。
/// </summary>
internal static class DevShopRemovalSelfTest
{
    public static async Task<AgentToolResult> RunAsync()
    {
        var tcs = new TaskCompletionSource<AgentToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        MainThread.Post(() => _ = RunOnMainThreadAsync(tcs));
        return await tcs.Task.ConfigureAwait(false);
    }

    private static async Task RunOnMainThreadAsync(TaskCompletionSource<AgentToolResult> tcs)
    {
        try
        {
            tcs.TrySetResult(await RunCoreAsync().ConfigureAwait(true));
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[SelfTest] shop removal failed: {ex}");
            tcs.TrySetResult(AgentToolResult.Error($"Self-test failed: {ex.Message}"));
        }
    }

    private static async Task<AgentToolResult> RunCoreAsync()
    {
        var run = RunManager.Instance;
        var aiPlayer = AiTeammateManager.Instance?.AiPlayer;
        if (run == null || !run.IsInProgress || aiPlayer == null)
        {
            return AgentToolResult.Error(
                "需要一局进行中的对局且 AI 玩家已入局（先 sts2_host+添加AI+开始，或单人爬塔模式）。");
        }

        var state = run.DebugOnlyGetState();
        if (state == null || !state.Players.Contains(aiPlayer))
            return AgentToolResult.Error("AI 玩家不在当前对局里。");

        var local = LocalContext.NetId is ulong localId ? state.GetPlayer(localId) : null;
        var humanIsAi = local == null || local.NetId == aiPlayer.NetId;

        var aiDeckBefore = DeckEntries(aiPlayer);
        var humanDeckBefore = humanIsAi ? new List<string>() : DeckEntries(local!);
        var aiGoldBefore = aiPlayer.Gold;
        var humanGoldBefore = humanIsAi ? 0 : local!.Gold;
        var overlaysBefore = OverlayCount();

        // 和 AutoShop 一样替 AI 建一份自己的商店库存（只取删牌项，不碰真人 UI）。
        var inventory = MerchantInventory.CreateForNormalMerchant(aiPlayer);
        var entry = inventory.CardRemovalEntry;
        if (entry == null)
            return AgentToolResult.Error("AI 的商店库存里没有删牌项。");

        var cost = entry.Cost;
        var log = new List<string>();
        var purchased = await AiCardShopRemoval.TryBuyAsync(aiPlayer, entry, msg => log.Add(msg));

        var aiDeckAfter = DeckEntries(aiPlayer);
        var humanDeckAfter = humanIsAi ? new List<string>() : DeckEntries(local!);
        var aiGoldAfter = aiPlayer.Gold;
        var humanGoldAfter = humanIsAi ? 0 : local!.Gold;
        var overlaysAfter = OverlayCount();

        var removed = MultisetRemoved(aiDeckBefore, aiDeckAfter);
        var reasons = new List<string>();
        if (!purchased) reasons.Add("购买返回 false");
        if (aiGoldBefore - aiGoldAfter != cost) reasons.Add($"AI 金币变化 {aiGoldBefore - aiGoldAfter} ≠ 价格 {cost}");
        if (aiDeckBefore.Count - aiDeckAfter.Count != 1) reasons.Add($"AI 牌组应少 1 张，实际 {aiDeckBefore.Count}→{aiDeckAfter.Count}");
        if (removed.Count != 1) reasons.Add($"应从 AI 牌组删掉恰好 1 张，实际 {removed.Count}");
        if (!entry.Used) reasons.Add("删牌项没有标记已用");
        if (!humanIsAi)
        {
            if (humanGoldBefore != humanGoldAfter) reasons.Add($"真人金币被动了 {humanGoldBefore}→{humanGoldAfter}");
            if (!MultisetEquals(humanDeckBefore, humanDeckAfter)) reasons.Add("真人牌组被动了");
            if (overlaysAfter != overlaysBefore) reasons.Add($"真人被弹了界面（overlay {overlaysBefore}→{overlaysAfter}）");
        }

        var json = new JsonObject
        {
            ["pass"] = reasons.Count == 0,
            ["purchased"] = purchased,
            ["cost"] = cost,
            ["ai_gold_before"] = aiGoldBefore,
            ["ai_gold_after"] = aiGoldAfter,
            ["ai_deck_before"] = aiDeckBefore.Count,
            ["ai_deck_after"] = aiDeckAfter.Count,
            ["removed_cards"] = ToJsonArray(removed),
            ["removals_used"] = aiPlayer.ExtraFields.CardShopRemovalsUsed,
            ["entry_used"] = entry.Used,
            ["human_is_ai"] = humanIsAi,
            ["human_gold_before"] = humanGoldBefore,
            ["human_gold_after"] = humanGoldAfter,
            ["human_deck_before"] = humanDeckBefore.Count,
            ["human_deck_after"] = humanDeckAfter.Count,
            ["overlays_before"] = overlaysBefore,
            ["overlays_after"] = overlaysAfter,
            ["log"] = ToJsonArray(log),
            ["failed_assertions"] = ToJsonArray(reasons)
        };
        return AgentToolResult.Ok(json.ToJsonString(AgentJson.Options));
    }

    private static List<string> DeckEntries(Player player)
    {
        try
        {
            return PileType.Deck.GetPile(player).Cards.Select(c => c.Id.Entry).ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>按“多重集”求 before - after：同名卡（两张打击）也算删掉了一张。</summary>
    private static List<string> MultisetRemoved(List<string> before, List<string> after)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var id in after)
            counts[id] = counts.GetValueOrDefault(id) + 1;

        var removed = new List<string>();
        foreach (var id in before)
        {
            if (counts.TryGetValue(id, out var n) && n > 0)
                counts[id] = n - 1;
            else
                removed.Add(id);
        }
        return removed;
    }

    private static bool MultisetEquals(List<string> left, List<string> right)
    {
        if (left.Count != right.Count)
            return false;
        return MultisetRemoved(left, right).Count == 0;
    }

    private static int OverlayCount()
    {
        try { return NOverlayStack.Instance?.ScreenCount ?? 0; }
        catch { return 0; }
    }

    private static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        var arr = new JsonArray();
        foreach (var v in values) arr.Add(v);
        return arr;
    }
}
