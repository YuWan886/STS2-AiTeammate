using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Gold;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;

namespace AiTeammate.AiTeammateCode.Decision;

/// <summary>
/// 商店「删牌」必须替 AI 自己做一遍。游戏的 <c>MerchantCardRemovalEntry.OnTryPurchase</c> 最终调用
/// <c>OneOffSynchronizer.DoLocalMerchantCardRemoval</c> → <c>DoMerchantCardRemoval(LocalPlayer, ...)</c>：
/// 它不看 <c>MerchantEntry._player</c>，永远删「本地玩家」的牌、扣本地玩家的金币，并把选牌界面弹给本地玩家。
/// AI 直接走那条路就会删到真人头上；真人也正好在删牌时，两个流程并发，后确认的那个拿到已经离开牌堆的
/// 卡（Pile == null），<c>NetDeckCard.FromModel</c> 抛 “card without a pile”，整笔商店交易被打断。
/// （2026-11-03 的 godot 日志 7448–7462 行就是这个现场。）
/// </summary>
internal static class AiCardShopRemoval
{
    /// <summary>
    /// 替 <paramref name="aiPlayer"/> 买下这次删牌：AI 自己选牌、自己付钱、自己进 <c>CardShopRemovalsUsed</c>。
    /// 返回 false 表示没买成（牌堆里没有可删的牌 / 钱不够 / 已用过），调用方按购买失败处理即可。
    /// </summary>
    public static async Task<bool> TryBuyAsync(
        Player aiPlayer,
        MerchantCardRemovalEntry entry,
        Action<string>? log = null)
    {
        var cost = entry.Cost;
        if (cost > aiPlayer.Gold)
        {
            log?.Invoke($"删牌买不起（{cost} > {aiPlayer.Gold}）");
            return false;
        }

        var prefs = new CardSelectorPrefs(CardSelectorPrefs.RemoveSelectionPrompt, 1)
        {
            Cancelable = true,
            RequireManualConfirmation = true
        };

        // 只碰 AI 自己的牌堆。FromDeckGeneric 的 AI 前缀会在本地直接挑选，不会给真人弹选牌界面。
        var card = (await CardSelectCmd.FromDeckForRemoval(aiPlayer, prefs)).FirstOrDefault();
        if (card == null)
        {
            log?.Invoke("删牌取消（没有可删除的牌）");
            return false;
        }

        await PlayerCmd.LoseGold(cost, aiPlayer, GoldLossType.Spent);
        await CardPileCmd.RemoveFromDeck(card);
        aiPlayer.ExtraFields.CardShopRemovalsUsed++;
        entry.SetUsed();
        log?.Invoke($"删牌完成: [{card.Title}] 花费 {cost} → 剩余 {aiPlayer.Gold} 金");
        return true;
    }
}
