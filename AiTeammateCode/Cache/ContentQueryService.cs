using System.Text;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

namespace AiTeammate.AiTeammateCode.Cache;

public class ContentQueryService
{
    private readonly ContentCacheService _cache;

    public ContentQueryService(ContentCacheService cache)
    {
        _cache = cache;
    }

    public CardIndexEntry? GetCardBrief(string cardId)
    {
        if (_cache.Index == null) return null;
        _cache.Index.Cards.TryGetValue(cardId, out var entry);
        return entry;
    }

    public CardFullEntry? GetCardFull(string cardId)
    {
        if (_cache.Full == null) return null;
        _cache.Full.Cards.TryGetValue(cardId, out var entry);
        return entry;
    }

    public RelicFullEntry? GetRelicFull(string relicId)
    {
        if (_cache.Full == null) return null;
        _cache.Full.Relics.TryGetValue(relicId, out var entry);
        return entry;
    }

    public PotionFullEntry? GetPotionFull(string potionId)
    {
        if (_cache.Full == null) return null;
        _cache.Full.Potions.TryGetValue(potionId, out var entry);
        return entry;
    }

    public string LookupAndFormatCard(string cardId, bool isUpgraded = false)
    {
        var full = GetCardFull(cardId);
        var brief = GetCardBrief(cardId);
        if (full == null && brief == null) return $"[{cardId}: 无数据]";
        return FormatCardEntry(cardId, brief, full, isUpgraded);
    }

    // ---- Enchantment queries ----

    public EnchantIndexEntry? GetEnchantBrief(string enchantId)
    {
        if (_cache.Index == null) return null;
        _cache.Index.Enchants.TryGetValue(enchantId, out var entry);
        return entry;
    }

    public EnchantFullEntry? GetEnchantFull(string enchantId)
    {
        if (_cache.Full == null) return null;
        _cache.Full.Enchants.TryGetValue(enchantId, out var entry);
        return entry;
    }

    public string LookupAndFormatEnchant(string enchantId)
    {
        var full = GetEnchantFull(enchantId);
        var brief = GetEnchantBrief(enchantId);
        if (full == null && brief == null) return $"[附魔 {enchantId}: 无数据]";
        return $"[附魔 {full?.Name ?? brief?.Name}] {full?.Description ?? brief?.OneLiner}";
    }

    public string FormatCardKnowledge(IEnumerable<CardModel> cards, int maxCount = 8)
    {
        if (_cache.Index == null || _cache.Full == null) return "";

        var lines = cards
            .Where(card => card != null)
            .DistinctBy(card => card.CanonicalInstance.Id.Entry)
            .Take(maxCount)
            .Select(card => LookupAndFormatCard(card.CanonicalInstance.Id.Entry, card.IsUpgraded))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

        return lines.Count > 0 ? string.Join(Environment.NewLine, lines) : "";
    }

    // ---- Upgrade preview ----

    /// <summary>
    /// "升级后: ..." for a card that can still be upgraded (content cache first, the game's own
    /// upgrade preview as fallback). Empty when upgrading changes nothing visible or the card cannot
    /// be upgraded. 卡牌升级后会变成什么；供提示词和 card_info 使用。
    /// </summary>
    public string FormatUpgradePreview(CardModel? card, int maxLen = 0)
    {
        if (card == null || card.IsUpgraded || !card.IsUpgradable) return "";

        try
        {
            var full = GetCardFull(card.CanonicalInstance.Id.Entry);
            if (full != null)
            {
                var cached = ComposeUpgradePreview(full.Description, full.UpgradedDescription,
                    full.CostText, full.UpgradeCostText, maxLen);
                if (!string.IsNullOrWhiteSpace(cached)) return cached;
            }

            // Cache miss (modded card that appeared after generation) or the cache says the upgrade
            // changes nothing: ask the game and only report a real difference.
            var live = PromptTextHelper.ResolveUpgradePreview(card);
            if (string.IsNullOrWhiteSpace(live)) return "";
            var current = PromptTextHelper.GetCardDescriptionText(card, PileType.Hand);
            return ComposeUpgradePreview(current, live, "", "", maxLen);
        }
        catch { return ""; }
    }

    /// <summary>Upgrade preview by card name, for card_info calls about cards outside the deck. 按名字查升级预览。</summary>
    public string FormatUpgradePreviewByName(string name, int maxLen = 0)
    {
        var full = FindCardByName(name);
        return full == null
            ? ""
            : ComposeUpgradePreview(full.Description, full.UpgradedDescription,
                full.CostText, full.UpgradeCostText, maxLen);
    }

    public CardFullEntry? FindCardByName(string name)
    {
        if (_cache.Full == null || string.IsNullOrWhiteSpace(name)) return null;

        var needle = name.Trim();
        foreach (var entry in _cache.Full.Cards.Values)
        {
            if (string.Equals(entry.Name, needle, StringComparison.OrdinalIgnoreCase)
                || string.Equals(entry.UpgradedName, needle, StringComparison.OrdinalIgnoreCase)
                || string.Equals(entry.Name + "+", needle, StringComparison.OrdinalIgnoreCase))
                return entry;
        }
        return null;
    }

    private static string ComposeUpgradePreview(string baseDesc, string upgradedDesc,
        string costText, string upgradedCostText, int maxLen)
    {
        var descChanged = !string.IsNullOrWhiteSpace(upgradedDesc)
                          && !string.Equals(upgradedDesc, baseDesc, StringComparison.Ordinal);
        var costChanged = !string.IsNullOrWhiteSpace(upgradedCostText)
                          && !string.Equals(upgradedCostText, costText, StringComparison.Ordinal);
        if (!descChanged && !costChanged) return "";

        var sb = new StringBuilder("升级后: ");
        if (costChanged) sb.Append(upgradedCostText).Append(" | ");
        if (descChanged) sb.Append(upgradedDesc);
        var text = sb.ToString().TrimEnd(' ', '|');
        return maxLen > 0 ? PromptTextHelper.Truncate(text, maxLen) : text;
    }

    public string FormatRelicKnowledge(IEnumerable<RelicModel> relics, int maxCount = 6)
    {
        if (_cache.Index == null || _cache.Full == null) return "";

        var lines = relics
            .Where(relic => relic != null)
            .DistinctBy(relic => relic.CanonicalInstance.Id.Entry)
            .Take(maxCount)
            .Select(relic => FormatRelicEntry(relic.CanonicalInstance.Id.Entry))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

        return lines.Count > 0 ? string.Join(Environment.NewLine, lines) : "";
    }

    public string FormatPotionKnowledge(IEnumerable<PotionModel> potions, int maxCount = 4)
    {
        if (_cache.Index == null || _cache.Full == null) return "";

        var lines = potions
            .Where(potion => potion != null)
            .DistinctBy(potion => potion.CanonicalInstance.Id.Entry)
            .Take(maxCount)
            .Select(potion => FormatPotionEntry(potion.CanonicalInstance.Id.Entry))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

        return lines.Count > 0 ? string.Join(Environment.NewLine, lines) : "";
    }

    public string FormatMentionKnowledge(IEnumerable<string> texts, int maxCount = 8)
    {
        if (_cache.Index == null) return "";

        var combinedText = string.Join(" ", texts.Where(text => !string.IsNullOrWhiteSpace(text)));
        if (string.IsNullOrWhiteSpace(combinedText))
            return "";

        var sb = new StringBuilder();
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var emitted = 0;

        emitted += AppendCardMentions(sb, combinedText, found, maxCount - emitted);
        emitted += AppendEnchantMentions(sb, combinedText, found, maxCount - emitted);
        emitted += AppendRelicMentions(sb, combinedText, found, maxCount - emitted);
        emitted += AppendPotionMentions(sb, combinedText, found, maxCount - emitted);

        return sb.ToString().TrimEnd();
    }

    // ---- Knowledge enrichment: preemptively look up relevant game objects ----

    /// <summary>
    /// Scans text for known game object IDs (cards, relics, enchantments, etc.)
    /// and returns formatted descriptions for any that are found.
    /// Used to preemptively provide the AI with information it might need.
    /// Also scans by localized name since event options use names not IDs.
    /// </summary>
    public string EnrichContext(string text)
    {
        return FormatMentionKnowledge(new[] { text });
    }

    private int AppendCardMentions(StringBuilder sb, string text, HashSet<string> found, int remaining)
    {
        if (_cache.Index == null || remaining <= 0) return 0;
        var emitted = 0;
        foreach (var kv in _cache.Index.Cards)
        {
            if (remaining <= 0) break;
            if (!ContainsMention(text, kv.Key, kv.Value.Name) || !found.Add(kv.Key))
                continue;

            sb.AppendLine(LookupAndFormatCard(kv.Key));
            emitted++;
            remaining--;
        }
        return emitted;
    }

    private int AppendEnchantMentions(StringBuilder sb, string text, HashSet<string> found, int remaining)
    {
        if (_cache.Index == null || remaining <= 0) return 0;
        var emitted = 0;
        foreach (var kv in _cache.Index.Enchants)
        {
            if (remaining <= 0) break;
            if (!ContainsMention(text, kv.Key, kv.Value.Name) || !found.Add(kv.Key))
                continue;

            sb.AppendLine(LookupAndFormatEnchant(kv.Key));
            emitted++;
            remaining--;
        }
        return emitted;
    }

    private int AppendRelicMentions(StringBuilder sb, string text, HashSet<string> found, int remaining)
    {
        if (_cache.Index == null || remaining <= 0) return 0;
        var emitted = 0;
        foreach (var kv in _cache.Index.Relics)
        {
            if (remaining <= 0) break;
            if (!ContainsMention(text, kv.Key, kv.Value.Name) || !found.Add(kv.Key))
                continue;

            sb.AppendLine(FormatRelicEntry(kv.Key));
            emitted++;
            remaining--;
        }
        return emitted;
    }

    private int AppendPotionMentions(StringBuilder sb, string text, HashSet<string> found, int remaining)
    {
        if (_cache.Index == null || remaining <= 0) return 0;
        var emitted = 0;
        foreach (var kv in _cache.Index.Potions)
        {
            if (remaining <= 0) break;
            if (!ContainsMention(text, kv.Key, kv.Value.Name) || !found.Add(kv.Key))
                continue;

            sb.AppendLine(FormatPotionEntry(kv.Key));
            emitted++;
            remaining--;
        }
        return emitted;
    }

    private static bool ContainsMention(string text, string id, string name)
    {
        if (text.Contains(id, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.IsNullOrWhiteSpace(name) && name.Length >= 2 && text.Contains(name, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private string FormatCardEntry(string cardId, CardIndexEntry? brief, CardFullEntry? full, bool isUpgraded = false)
    {
        // The cache keeps both variants; an upgraded card must be described (and priced) as the
        // upgraded one, otherwise "全身撞击+" still reads 1 Energy / 6 damage.
        var name = isUpgraded && !string.IsNullOrWhiteSpace(full?.UpgradedName)
            ? full!.UpgradedName
            : (full?.Name ?? brief?.Name ?? cardId);

        // Use the correct description based on upgrade state: if upgraded and we have an
        // upgraded description that differs from the base, use the upgraded one.
        var baseDesc = full?.Description ?? brief?.OneLiner ?? "";
        var upgradeDesc = full?.UpgradedDescription;
        var desc = (isUpgraded && !string.IsNullOrWhiteSpace(upgradeDesc) && !string.Equals(upgradeDesc, baseDesc, StringComparison.Ordinal))
            ? upgradeDesc
            : baseDesc;

        var costText = isUpgraded && !string.IsNullOrWhiteSpace(full?.UpgradeCostText)
            ? full!.UpgradeCostText
            : (full?.CostText ?? brief?.CostText);

        var sb = new StringBuilder();
        sb.Append(name);
        if (!string.IsNullOrWhiteSpace(costText))
            sb.Append(" | ").Append(costText);
        if (!string.IsNullOrWhiteSpace(full?.TargetType))
            sb.Append(" | 目标:").Append(full.TargetType);
        if (!string.IsNullOrWhiteSpace(desc))
            sb.Append(" | 效果:").Append(desc);
        return sb.ToString();
    }

    private string FormatRelicEntry(string relicId)
    {
        if (_cache.Index == null) return "";
        _cache.Index.Relics.TryGetValue(relicId, out var brief);
        var full = GetRelicFull(relicId);
        if (brief == null && full == null) return "";

        var desc = full?.Description ?? brief?.OneLiner ?? "";
        return $"[遗物] {full?.Name ?? brief?.Name} | 效果:{desc}";
    }

    private string FormatPotionEntry(string potionId)
    {
        if (_cache.Index == null) return "";
        _cache.Index.Potions.TryGetValue(potionId, out var brief);
        var full = GetPotionFull(potionId);
        if (brief == null && full == null) return "";

        var desc = full?.Description ?? brief?.OneLiner ?? "";
        if (string.IsNullOrWhiteSpace(desc))
            return $"[药水] {full?.Name ?? brief?.Name}";
        var target = PromptTextHelper.DescribePotionTarget(full?.TargetType);
        var targetText = string.IsNullOrWhiteSpace(target) ? "" : $" | 目标:{target}";
        return $"[药水] {full?.Name ?? brief?.Name}{targetText} | 效果:{desc}";
    }
}
