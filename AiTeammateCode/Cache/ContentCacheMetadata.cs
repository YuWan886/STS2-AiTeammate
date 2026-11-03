using MegaCrit.Sts2.Core.Models;
using AiTeammate.AiTeammateCode.Localization;

namespace AiTeammate.AiTeammateCode.Cache;

internal static class ContentCacheMetadata
{
    public static string FormatCardCost(CardModel card)
    {
        // Not Canonical: that is the printed cost and stays put when an upgrade lowers it, which is
        // why Body Slam+ used to be cached as 1 Energy instead of 0.
        var energyCost = card.EnergyCost.CostsX
            ? AiLoc.Pick("X费", "X Energy")
            : $"{PromptTextHelper.ResolveEnergyCost(card)}{AiLoc.Pick("费", " Energy")}";

        if (card.HasStarCostX)
            return $"{energyCost}/X{AiLoc.Pick("辉星", " Star")}";

        var starCost = card.GetStarCostWithModifiers();
        if (starCost >= 0)
            return $"{energyCost}/{starCost}{AiLoc.Pick("辉星", " Star")}";

        return energyCost;
    }
}
