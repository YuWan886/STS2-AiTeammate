using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;

namespace AiTeammate.AiTeammateCode.Cache;

public class RelicCacheGenerator
{
    // Same icon token the game's RelicModel.DynamicDescription uses; PromptTextHelper.CleanText
    // strips the [img] BBCode and the res:// path when this fallback is turned into prompt text.
    private const string StarIcon = "[img]res://images/packed/sprite_fonts/star_icon.png[/img]";

    public Dictionary<string, RelicIndexEntry> GenerateIndex()
    {
        var entries = new Dictionary<string, RelicIndexEntry>();
        foreach (var relic in ModelDb.AllRelics)
        {
            var id = relic.Id.Entry;
            if (string.IsNullOrWhiteSpace(id)) continue;

            try
            {
                entries[id] = new RelicIndexEntry
                {
                    Name = ResolveTitle(relic),
                    Tier = relic.Rarity.ToString(),
                    OneLiner = ExtractOneLiner(ResolveDescription(relic)),
                    Pool = ResolvePoolId(relic),
                    PoolId = ResolvePoolId(relic)
                };
            }
            catch (Exception ex)
            {
                // One modded relic must never abort the whole cache (which used to leave the AI
                // with no card/relic knowledge at all); log it and keep the rest.
                MainFile.Logger.Warn($"[Cache] relic '{id}' skipped: {ex.Message}");
            }
        }
        return entries;
    }

    public Dictionary<string, RelicFullEntry> GenerateFull()
    {
        var entries = new Dictionary<string, RelicFullEntry>();
        foreach (var relic in ModelDb.AllRelics)
        {
            var id = relic.Id.Entry;
            if (string.IsNullOrWhiteSpace(id)) continue;

            try
            {
                entries[id] = new RelicFullEntry
                {
                    Name = ResolveTitle(relic),
                    Tier = relic.Rarity.ToString(),
                    Description = ResolveDescription(relic),
                    FlavorText = PromptTextHelper.ResolveLocString(TryGet(() => relic.Flavor), ""),
                    Pool = ResolvePoolId(relic),
                    PoolId = ResolvePoolId(relic),
                    MerchantCost = TryGetValue(() => relic.MerchantCost)
                };
            }
            catch (Exception ex)
            {
                MainFile.Logger.Warn($"[Cache] relic '{id}' skipped: {ex.Message}");
            }
        }
        return entries;
    }

    private static string ResolveTitle(RelicModel relic)
    {
        return PromptTextHelper.ResolveLocString(TryGet(() => relic.Title), relic.Id.Entry);
    }

    /// <summary>
    /// 0.111 resolves a relic description's energy icon through the relic's pool
    /// (<c>RelicModel.Pool = AllRelicPools.First(p => p.AllRelicIds.Contains(Id))</c>). A relic
    /// that belongs to no pool at all — a modded character's starting relic, for instance — makes
    /// that <c>First</c> throw. Fall back to the same description assembled without the pool.
    /// </summary>
    private static string ResolveDescription(RelicModel relic)
    {
        var dynamicDescription = TryGet(() => relic.DynamicDescription);
        if (dynamicDescription != null)
            return PromptTextHelper.ResolveLocString(dynamicDescription, "");

        return PromptTextHelper.ResolveLocString(TryGet(() => BuildDescriptionWithoutPool(relic)), "");
    }

    private static LocString BuildDescriptionWithoutPool(RelicModel relic)
    {
        var description = new LocString("relics", relic.Id.Entry + ".description");
        relic.DynamicVars.AddTo(description);
        description.Add("energyPrefix", "");
        description.Add("singleStarIcon", StarIcon);
        return description;
    }

    private static string ResolvePoolId(RelicModel relic)
    {
        return TryGet(() => relic.Pool)?.Id.Entry ?? "";
    }

    private static T? TryGet<T>(Func<T?> getter) where T : class
    {
        try { return getter(); }
        catch { return null; }
    }

    private static int TryGetValue(Func<int> getter)
    {
        try { return getter(); }
        catch { return 0; }
    }

    private string ExtractOneLiner(string description)
    {
        var periodIdx = description.IndexOf('.');
        if (periodIdx > 0) return description.Substring(0, periodIdx + 1);
        return description.Length > 60 ? description.Substring(0, 60) + "..." : description;
    }
}
