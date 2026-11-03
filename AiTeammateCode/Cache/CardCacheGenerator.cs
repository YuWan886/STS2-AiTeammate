using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;

namespace AiTeammate.AiTeammateCode.Cache;

public class CardCacheGenerator
{
    public Dictionary<string, CardIndexEntry> GenerateIndex()
    {
        var entries = new Dictionary<string, CardIndexEntry>();
        var allCards = ModelDb.AllCards;
        foreach (var card in allCards)
        {
            var id = card.Id.Entry;
            if (string.IsNullOrWhiteSpace(id)) continue;

            try
            {
                var description = PromptTextHelper.ResolveCardDescription(card, PileType.Hand);
                var character = ResolveCharacter(card);
                entries[id] = new CardIndexEntry
                {
                    Name = card.Title,
                    Cost = PromptTextHelper.ResolveEnergyCost(card),
                    CostText = ContentCacheMetadata.FormatCardCost(card),
                    Type = card.Type.ToString(),
                    Rarity = card.Rarity.ToString(),
                    OneLiner = ExtractOneLiner(description),
                    Character = character != null ? PromptTextHelper.ResolveLocString(character.Title, character.Id.Entry) : "",
                    CharacterId = character?.Id.Entry ?? "",
                    Pool = ResolvePoolTitle(card),
                    PoolId = ResolvePoolId(card),
                    Keywords = card.Keywords.Select(k => k.ToString()).OrderBy(k => k).ToList(),
                    Tags = card.Tags.Select(t => t.ToString()).OrderBy(t => t).ToList()
                };
            }
            catch (Exception ex)
            {
                // One broken modded card must never abort the whole content cache.
                MainFile.Logger.Warn($"[Cache] card '{id}' skipped: {ex.Message}");
            }
        }
        return entries;
    }

    public Dictionary<string, CardFullEntry> GenerateFull()
    {
        var entries = new Dictionary<string, CardFullEntry>();
        var allCards = ModelDb.AllCards;
        foreach (var card in allCards)
        {
            var id = card.Id.Entry;
            if (string.IsNullOrWhiteSpace(id)) continue;

            try
            {
                var description = PromptTextHelper.ResolveCardDescription(card, PileType.Hand);
                var upgradedCard = CreateUpgradedPreview(card);
                var upgradedDescription = upgradedCard != null
                    ? PromptTextHelper.ResolveCardDescription(upgradedCard, PileType.Hand)
                    : "";
                var character = ResolveCharacter(card);
                entries[id] = new CardFullEntry
                {
                    Name = card.Title,
                    UpgradedName = upgradedCard?.Title ?? "",
                    Cost = PromptTextHelper.ResolveEnergyCost(card),
                    CostText = ContentCacheMetadata.FormatCardCost(card),
                    UpgradeCostText = upgradedCard != null ? ContentCacheMetadata.FormatCardCost(upgradedCard) : "",
                    Type = card.Type.ToString(),
                    Rarity = card.Rarity.ToString(),
                    Description = description,
                    UpgradedDescription = upgradedDescription,
                    Keywords = card.Keywords?.Select(k => k.ToString()).ToList() ?? new(),
                    Tags = card.Tags?.Select(t => t.ToString()).ToList() ?? new(),
                    TargetType = card.TargetType.ToString(),
                    Character = character != null ? PromptTextHelper.ResolveLocString(character.Title, character.Id.Entry) : "",
                    CharacterId = character?.Id.Entry ?? "",
                    Pool = ResolvePoolTitle(card),
                    PoolId = ResolvePoolId(card),
                    MultiplayerConstraint = card.MultiplayerConstraint.ToString(),
                    StarCost = card.CanonicalStarCost,
                    HasXCost = card.EnergyCost.CostsX,
                    HasXStarCost = card.HasStarCostX,
                    IsUpgradable = card.IsUpgradable
                };
            }
            catch (Exception ex)
            {
                MainFile.Logger.Warn($"[Cache] card '{id}' skipped: {ex.Message}");
            }
        }
        return entries;
    }

    private static CharacterModel? ResolveCharacter(CardModel card)
    {
        try
        {
            return ModelDb.AllCharacters.FirstOrDefault(character => character.CardPool.Id == card.Pool.Id);
        }
        catch
        {
            return null;
        }
    }

    private static string ResolvePoolTitle(CardModel card)
    {
        try { return card.Pool?.Title ?? ""; }
        catch { return ""; }
    }

    private static string ResolvePoolId(CardModel card)
    {
        try { return card.Pool?.Id.Entry ?? ""; }
        catch { return ""; }
    }

    private static CardModel? CreateUpgradedPreview(CardModel card)
    {
        if (!card.IsUpgradable)
            return null;

        try
        {
            var preview = card.CanonicalInstance.ToMutable();
            preview.UpgradeInternal();
            preview.FinalizeUpgradeInternal();
            return preview;
        }
        catch
        {
            return null;
        }
    }

    private string ExtractOneLiner(string description)
    {
        var periodIdx = description.IndexOf('.');
        if (periodIdx > 0) return description.Substring(0, periodIdx + 1);
        return description.Length > 50 ? description.Substring(0, 50) + "..." : description;
    }
}
