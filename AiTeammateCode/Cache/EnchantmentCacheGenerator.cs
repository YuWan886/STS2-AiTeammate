using MegaCrit.Sts2.Core.Models;

namespace AiTeammate.AiTeammateCode.Cache;

public class EnchantmentCacheGenerator
{
    public Dictionary<string, EnchantIndexEntry> GenerateIndex()
    {
        var entries = new Dictionary<string, EnchantIndexEntry>();
        var all = ModelDb.DebugEnchantments.ToList();
        foreach (var e in all)
        {
            var id = e.Id.Entry;
            if (string.IsNullOrWhiteSpace(id)) continue;

            try
            {
                var name = PromptTextHelper.ResolveLocString(e.Title, id);
                var description = PromptTextHelper.ResolveLocString(e.DynamicDescription, "");
                entries[id] = new EnchantIndexEntry
                {
                    Name = name,
                    OneLiner = ExtractOneLiner(description)
                };
            }
            catch (Exception ex)
            {
                MainFile.Logger.Warn($"[Cache] enchantment '{id}' skipped: {ex.Message}");
            }
        }
        return entries;
    }

    public Dictionary<string, EnchantFullEntry> GenerateFull()
    {
        var entries = new Dictionary<string, EnchantFullEntry>();
        var all = ModelDb.DebugEnchantments.ToList();
        foreach (var e in all)
        {
            var id = e.Id.Entry;
            if (string.IsNullOrWhiteSpace(id)) continue;

            try
            {
                var name = PromptTextHelper.ResolveLocString(e.Title, id);
                entries[id] = new EnchantFullEntry
                {
                    Name = name,
                    Description = PromptTextHelper.ResolveLocString(e.DynamicDescription, "")
                };
            }
            catch (Exception ex)
            {
                MainFile.Logger.Warn($"[Cache] enchantment '{id}' skipped: {ex.Message}");
            }
        }
        return entries;
    }

    private string ExtractOneLiner(string description)
    {
        var periodIdx = description.IndexOf('.');
        if (periodIdx > 0) return description[..(periodIdx + 1)];
        return description.Length > 60 ? description[..60] + "..." : description;
    }
}
