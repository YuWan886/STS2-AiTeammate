using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.AutoSlay;
using MegaCrit.Sts2.Core.AutoSlay.Helpers;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;
using AiTeammate.AiTeammateCode;

namespace AiTeammate.AiTeammateCode.Decision;

public class OutOfCombatExecutor
{
    private readonly DecisionService _decision;

    public OutOfCombatExecutor(DecisionService decision)
    {
        _decision = decision;
    }

    public async Task AutoShop(Player aiPlayer, CancellationToken ct)
    {
        await Task.Delay(500, ct);
        var log = AiTeammateManager.Instance.AiLog;
        log.Log("SHOP", "AI entering shop");

        // Skip shop if AI is dead — purchasing would cause NullReferenceException
        // because the player's card pile state is invalid when dead.
        if (!aiPlayer.Creature.IsAlive)
        {
            log.Log("SHOP", "AI is dead, skipping shop");
            return;
        }

        try
        {
            // Create inventory for AI player (game only creates for local player)
            var inventory = MerchantInventory.CreateForNormalMerchant(aiPlayer);
            var allEntries = inventory.AllEntries.ToList();
            var stockedEntries = allEntries.Where(e => e.IsStocked).ToList();
            log.Log("SHOP", $"AI shop inventory: {allEntries.Count} total, {stockedEntries.Count} stocked | Gold={aiPlayer.Gold}");

            if (stockedEntries.Count == 0)
            {
                log.Log("SHOP", "No stocked items");
                return;
            }

            // Get affordable items
            var affordable = allEntries
                .Where(e => e.IsStocked && e.Cost <= aiPlayer.Gold)
                .ToList();

            if (affordable.Count == 0)
            {
                var stillStocked = allEntries.Count(e => e.IsStocked);
                if (stillStocked == 0)
                    log.Log("SHOP", "Nothing left in stock");
                else
                    log.Log("SHOP", $"Nothing affordable ({stillStocked} items too expensive, Gold={aiPlayer.Gold})");
                return;
            }

            var shopEntries = affordable.Select(CreateShopDecisionEntry).ToList();
            log.Log("SHOP", $"{affordable.Count} affordable items, Gold={aiPlayer.Gold}");

            // Single LLM call: get all items to buy at once
            var (choices, reason) = await _decision.DecideShopAsync(aiPlayer, shopEntries, ct);

            if (!string.IsNullOrWhiteSpace(reason))
                log.Log("AI_THINK", $"[商店] {reason}");

            // Empty choices = leave shop
            if (choices.Count == 0)
            {
                log.Log("AI_ACTION", "离开商店");
                log.Log("SHOP", "AI chose to leave shop (empty selection)");
                log.Log("SHOP", "Shop complete");
                return;
            }

            // Validate and deduplicate choices
            var validChoices = choices
                .Where(c => c >= 0 && c < affordable.Count)
                .Distinct()
                .ToList();

            if (validChoices.Count == 0)
            {
                log.Log("AI_ACTION", "离开商店");
                log.Log("SHOP", "AI chose to leave shop (no valid choices)");
                log.Log("SHOP", "Shop complete");
                return;
            }

            // Execute purchases in order, tracking remaining gold
            int purchased = 0;
            foreach (var idx in validChoices)
            {
                ct.ThrowIfCancellationRequested();

                var chosen = affordable[idx];

                // Check if still affordable (gold may have been spent on previous purchases)
                if (chosen.Cost > aiPlayer.Gold)
                {
                    log.Log("SHOP", $"Skipping {shopEntries[idx].DisplayText} — cost {chosen.Cost} exceeds remaining gold {aiPlayer.Gold}");
                    continue;
                }

                // Check if still stocked
                if (!chosen.IsStocked)
                {
                    log.Log("SHOP", $"Skipping {shopEntries[idx].DisplayText} — no longer in stock");
                    continue;
                }

                log.Log("AI_ACTION", $"购买: {shopEntries[idx].DisplayText}");
                log.Log("SHOP", $"AI buying: {chosen.GetType().Name} for {chosen.Cost} gold");

                // 删牌绝不能走 MerchantCardRemovalEntry.OnTryPurchase：那条路固定作用在 LocalPlayer
                // （真人）身上，会删真人的牌、扣真人的钱，还会给真人弹一个选牌界面。详见 AiCardShopRemoval。
                bool success = chosen is MerchantCardRemovalEntry removalEntry
                    ? await AiCardShopRemoval.TryBuyAsync(aiPlayer, removalEntry, msg => log.Log("SHOP", msg))
                    : await chosen.OnTryPurchaseWrapper(inventory, false);
                log.Log("SHOP", $"Purchase result: {(success ? "success" : "failed")} | Gold remaining: {aiPlayer.Gold}");

                if (success)
                    purchased++;
                else
                    break; // Stop on purchase failure

                await Task.Delay(200, ct);
            }

            log.Log("SHOP", $"Shop complete — purchased {purchased} item(s)");
        }
        catch (Exception ex)
        {
            log.LogError("SHOP", $"Shop error: {ex.Message}\n{ex.StackTrace}");
        }
    }

    /// <summary>Format a shop entry into a compact human-readable string for the AI prompt.</summary>
    internal static ShopDecisionEntry CreateShopDecisionEntry(MerchantEntry e)
    {
        var price = $"[{e.Cost}金]";
        if (e is MerchantCardEntry cardEntry)
        {
            var card = cardEntry.CreationResult?.Card;
            var cardName = card?.Title ?? "?";
            // Include energy cost (and starlight cost if applicable)
            var costInfo = "";
            try
            {
                if (card != null)
                {
                    var energyCost = PromptTextHelper.ResolveEnergyCost(card);
                    var starCost = Math.Max(0, card.GetStarCostWithModifiers());
                    costInfo = starCost > 0 ? $"({energyCost}费/{starCost}辉星)" : $"({energyCost}费)";
                }
            }
            catch { }
            // Include card description for informed decisions
            string? cardDesc = null;
            try
            {
                if (card != null)
                {
                    cardDesc = PromptTextHelper.GetCardDescriptionText(card, MegaCrit.Sts2.Core.Entities.Cards.PileType.Hand);
                    cardDesc = CleanDescription(cardDesc);
                    if (cardDesc != null && cardDesc.Length > 80) cardDesc = cardDesc[..80] + "...";
                }
            }
            catch { }
            var descPart = cardDesc != null ? $" — {cardDesc}" : "";
            return new ShopDecisionEntry
            {
                DisplayText = $"{price} 卡牌:{cardName}{costInfo}{descPart}",
                Card = card,
                ModifyingRelics = cardEntry.CreationResult != null
                    ? cardEntry.CreationResult.ModifyingRelics.ToList()
                    : new List<RelicModel>()
            };
        }
        if (e is MerchantRelicEntry relicEntry)
        {
            var model = relicEntry.Model;
            var relicName = SafeLocText(model?.Title);
            var relicId = model?.Id.Entry ?? "?";
            if (relicName == "?" || relicName == relicId) relicName = relicId;
            string relicDesc = "?";
            try { relicDesc = SafeLocText(model?.DynamicDescription); }
            catch { /* DynamicVars may fail for AI, fall back to name only */ }
            relicDesc = CleanDescription(relicDesc);
            var descPart = relicDesc.Length > 0 ? $" — {relicDesc}" : "";
            if (descPart.Length > 80) descPart = descPart[..80] + "...";
            return new ShopDecisionEntry
            {
                DisplayText = $"{price} 遗物:{relicName}{descPart}",
                Relic = model
            };
        }
        if (e is MerchantPotionEntry potionEntry)
        {
            var model = potionEntry.Model;
            var potionName = SafeLocText(model?.Title);
            var potionId = model?.Id.Entry ?? "?";
            if (potionName == "?" || potionName == potionId) potionName = potionId;
            string potionDesc = "?";
            try { potionDesc = SafeLocText(model?.DynamicDescription); }
            catch { /* DynamicVars may fail for AI */ }
            potionDesc = CleanDescription(potionDesc);
            var descPart = potionDesc.Length > 0 ? $" — {potionDesc}" : "";
            if (descPart.Length > 80) descPart = descPart[..80] + "...";
            return new ShopDecisionEntry
            {
                DisplayText = $"{price} 药水:{potionName}{descPart}",
                Potion = model
            };
        }
        if (e is MerchantCardRemovalEntry)
        {
            return new ShopDecisionEntry
            {
                DisplayText = $"{price} 删牌",
                IsCardRemoval = true
            };
        }

        return new ShopDecisionEntry
        {
            DisplayText = $"{price} {e.GetType().Name}"
        };
    }

    /// <summary>Resolve shop text for prompts, preferring formatted text with concrete values.</summary>
    private static string SafeLocText(LocString? loc)
    {
        return PromptTextHelper.ResolveLocString(loc);
    }

    /// <summary>
    /// Clean a description string for AI prompt use:
    /// strips BBCode tags, Godot resource paths, unresolved placeholders, and collapses whitespace.
    /// </summary>
    private static string CleanDescription(string? raw)
    {
        var s = PromptTextHelper.CleanText(raw);
        if (s.Length == 0) return "";
        // Strip unresolved SmartFormat placeholders (e.g. {Amount:cond:...})
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\{[^}]+\}", "");
        return s.Trim();
    }
}
