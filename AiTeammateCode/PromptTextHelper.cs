using System.Text.RegularExpressions;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using AiTeammate.AiTeammateCode.Compat;
using AiTeammate.AiTeammateCode.Localization;

namespace AiTeammate.AiTeammateCode;

internal static class PromptTextHelper
{
    private static readonly Regex BbCodeRegex = new(@"\[.*?\]", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex ResourcePathRegex = new(@"res://\S+", RegexOptions.Compiled);
    private static readonly Regex PlaceholderRegex = new(@"\{[^{}\r\n]+\}", RegexOptions.Compiled);
    private static readonly Regex PlaceholderRootRegex = new(@"\{([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);
    private static readonly Regex PlaceholderTokenRegex = new(@"\{([A-Za-z_][A-Za-z0-9_-]*)[^{}\r\n]*\}", RegexOptions.Compiled);
    private static readonly string[] SuspiciousSuffixes =
    {
        "获得", "失去", "造成", "给予", "抽", "生成", "召唤", "保留", "打出", "获得在下个回合获得"
    };

    public static string ResolveLocString(LocString? loc, string fallback = "?")
    {
        if (loc == null) return fallback;

        // GetFormattedText() goes through LocManager.SmartFormat, which is not safe to run from a
        // worker thread: its shared object pool is corrupted when the UI formats at the same time.
        return OnMainThread(() =>
        {
            var raw = TryGet(() => loc.GetRawText());
            if (!ShouldAttemptFormatting(loc, raw))
                return PickBestText(raw, null, fallback: fallback);

            var formatted = TryGet(() => loc.GetFormattedText());
            return PickBestText(formatted, raw, fallback);
        }, fallback);
    }

    public static string ResolveCardDescription(CardModel? card, PileType pile = PileType.Hand, string fallback = "")
    {
        if (card == null) return fallback;

        return OnMainThread(() =>
        {
            // GetDescriptionForPile is the exact path the game UI uses: it adds every variable a
            // description may reference (OnTable/InCombat/TargetType/GainsBlock/energyPrefix/...),
            // including the conditional selectors introduced in 0.111 and the per-card extras from
            // AddExtraArgsToDescription. Rebuilding the LocString by hand misses those and makes
            // SmartFormat log a LocException ("No source extension could handle the selector named
            // ...") for every such card, so only fall back when that authoritative text is unusable.
            var pileDesc = TryGet(() => card.GetDescriptionForPile(pile));
            if (IsCompleteDescription(pileDesc))
                return CleanText(pileDesc);

            var offline = TryGet(() => FormatCardDescriptionOffline(card, card.IsUpgraded));
            var rawWithVars = TryGet(() => ApplyVariableFallbacks(card, card.IsUpgraded));
            var raw = TryGet(() => card.Description?.GetRawText());
            return PickBestText(pileDesc, offline, rawWithVars, raw, fallback: fallback);
        }, fallback);
    }

    /// <summary>
    /// Main-thread-safe <see cref="CardModel.GetDescriptionForPile"/>. Same SmartFormat hazard as
    /// <see cref="ResolveLocString"/>; returns an empty string when the text is unavailable.
    /// </summary>
    public static string GetCardDescriptionText(CardModel? card, PileType pile = PileType.Hand)
    {
        if (card == null) return "";

        return OnMainThread(() => card.GetDescriptionForPile(pile) ?? "", "");
    }

    /// <summary>
    /// The card's keyword / hover-tip sidebar as plain text ("标题: 说明；…"). The game renders this list
    /// next to a hovered card, and it is the ONLY place mod-added mechanics appear: the card text only
    /// prints the vanilla keyword words (Exhaust / Ethereal / ...) that
    /// <see cref="CardModel.GetDescriptionForPile"/> injects. Tips whose title already shows up in
    /// <paramref name="alreadyShown"/> (pass the description) are skipped so a normal prompt does not pay
    /// for explaining "Exhaust" on every card.
    /// 悬停侧边栏（词缀/新机制）的唯一文本来源；卡面描述里已有的不再重复。
    /// </summary>
    public static string GetHoverTipText(CardModel? card, int maxLen = 0, string? alreadyShown = null)
    {
        if (card == null) return "";

        return OnMainThread(() =>
        {
            try
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var parts = new List<string>();
                foreach (var tip in card.HoverTips)
                {
                    if (tip is not HoverTip hoverTip)
                        continue;

                    var title = CleanText(hoverTip.Title);
                    var description = CleanText(hoverTip.Description);
                    if (string.IsNullOrWhiteSpace(description))
                        continue;

                    // A tip is redundant when the card text already prints that very word ("消耗",
                    // "格挡", "易伤") — but only for the game's own content. A mod card commonly just
                    // NAMES its mechanic in the text while the explanation lives in the tip, so
                    // mod-sourced tips are always kept. 卡面只写关键词名、不解释；模组提示一律保留。
                    if (!string.IsNullOrWhiteSpace(title)
                        && alreadyShown != null
                        && IsVanillaSourced(hoverTip)
                        && alreadyShown.Contains(title, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var line = string.IsNullOrWhiteSpace(title) ? description : $"{title}: {description}";
                    if (!seen.Add(line))
                        continue;

                    parts.Add(line);
                }

                var text = string.Join("；", parts);
                if (string.IsNullOrWhiteSpace(text))
                    return "";
                return maxLen > 0 ? Truncate(text, maxLen) : text;
            }
            catch
            {
                // A broken mod hover tip must never break the prompt.
                return "";
            }
        }, "");
    }

    /// <summary>
    /// True when a tip describes the game's own content (no canonical model = static / keyword tips, or a
    /// model from the game assembly). Mod-sourced tips (custom powers, enchantments, afflictions) return
    /// false so their explanation survives even when the card text already names the mechanic.
    /// 提示是否来自原版内容；模组内容一律返回 false。
    /// </summary>
    private static bool IsVanillaSourced(HoverTip tip)
    {
        try
        {
            var model = tip.CanonicalModel;
            return model == null || model.GetType().Assembly == typeof(CardModel).Assembly;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Current (upgrade-adjusted) energy cost. <see cref="CardEnergyCost.Canonical"/> is the cost
    /// printed on the card and does NOT change when an upgrade lowers it (Body Slam 1→0, Apotheosis
    /// 2→1, ...), so it must never be used to decide whether a card is playable or to tell the model
    /// what the card costs right now.
    /// </summary>
    public static int ResolveEnergyCost(CardModel? card)
    {
        if (card == null) return 0;

        try { return Math.Max(0, card.EnergyCost.GetWithModifiers(CostModifiers.All)); }
        catch { return Math.Max(0, card.EnergyCost?.Canonical ?? 0); }
    }

    /// <summary>
    /// "What this card becomes after upgrading", straight from the game (the upgraded branch of the
    /// description with BBCode stripped). Empty when the card cannot be upgraded, already is, or the
    /// description carries unresolved placeholders.
    /// </summary>
    public static string ResolveUpgradePreview(CardModel? card, string fallback = "")
    {
        if (card == null || card.IsUpgraded || !card.IsUpgradable) return fallback;

        return OnMainThread(() =>
        {
            try { return CleanText(card.GetDescriptionForUpgradePreview()); }
            catch { return ""; }
        }, fallback);
    }

    private static string OnMainThread(Func<string> getter, string fallback)
    {
        try
        {
            return MainThread.Invoke(getter) ?? fallback;
        }
        catch
        {
            // Main thread busy/not available, or the game text threw: keep the prompt building.
            return fallback;
        }
    }

    public static string CleanText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        var cleaned = BbCodeRegex.Replace(text, "");
        cleaned = ResourcePathRegex.Replace(cleaned, "");
        cleaned = cleaned.Replace("\r", "");
        cleaned = WhitespaceRegex.Replace(cleaned, " ").Trim();
        return cleaned;
    }

    public static string Truncate(string text, int maxLen)
    {
        if (maxLen <= 0 || text.Length <= maxLen) return text;
        return text[..maxLen] + "...";
    }

    private static string PickBestText(string? primary, string? secondary, string? tertiary = null, string? quaternary = null, string fallback = "?")
    {
        var candidates = new[] { primary, secondary, tertiary, quaternary }
            .Select(CleanText)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();

        if (candidates.Count == 0) return fallback;

        var resolved = candidates.FirstOrDefault(s => !PlaceholderRegex.IsMatch(s) && !LooksMalformedCardText(s));
        if (!string.IsNullOrWhiteSpace(resolved))
            return resolved;

        resolved = candidates.FirstOrDefault(s => !PlaceholderRegex.IsMatch(s));
        return resolved ?? candidates[0];
    }

    private static bool LooksMalformedCardText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return true;

        if (PlaceholderRegex.IsMatch(text))
            return true;

        return SuspiciousSuffixes.Any(suffix => text.EndsWith(suffix, StringComparison.Ordinal));
    }

    /// <summary>True when the text is real prose with no leftover template placeholders.</summary>
    private static bool IsCompleteDescription(string? text)
    {
        return !string.IsNullOrWhiteSpace(text) && !LooksMalformedCardText(text);
    }

    private static bool ShouldAttemptFormatting(LocString loc, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || loc.Variables.Count == 0)
            return false;

        var placeholderRoots = PlaceholderRootRegex.Matches(raw)
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (placeholderRoots.Count == 0)
            return false;

        return placeholderRoots.All(name => loc.Variables.ContainsKey(name));
    }

    private static string? TryGet(Func<string?> getter)
    {
        try { return getter(); }
        catch { return null; }
    }

    /// <summary>
    /// Formats a card description without a live combat context. Only used when the in-game path
    /// (<see cref="CardModel.GetDescriptionForPile"/>) produced nothing usable. Returns null when
    /// the description references selectors this builder cannot supply — the conditional syntax
    /// 0.111 introduced ({CardType:...}, {GainsBlock:...}, ...) and per-card extras supplied by
    /// AddExtraArgsToDescription — because SmartFormat would then log a LocException per card.
    /// </summary>
    private static string? FormatCardDescriptionOffline(CardModel card, bool upgraded)
    {
        var description = BuildOfflineDescription(card, upgraded);
        var raw = description.GetRawText();
        if (!ShouldAttemptFormatting(description, raw))
            return null;

        return description.GetFormattedText();
    }

    private static LocString BuildOfflineDescription(CardModel card, bool upgraded)
    {
        var description = new LocString("cards", card.Id.Entry + ".description");
        card.DynamicVars.AddTo(description);
        description.Add(new IfUpgradedVar(upgraded ? UpgradeDisplay.Upgraded : UpgradeDisplay.Normal));
        description.Add("OnTable", true);
        description.Add("InCombat", false);
        description.Add("IsTargeting", false);
        description.Add("TargetType", card.TargetType.ToString());
        description.Add("energyPrefix", "");
        description.Add("singleStarIcon", AiLoc.Pick("辉星", "Star"));

        foreach (var variable in description.Variables)
        {
            if (variable.Value is EnergyVar energyVar)
                energyVar.ColorPrefix = "";
        }

        return description;
    }

    private static string ApplyVariableFallbacks(CardModel card, bool upgraded)
    {
        var description = BuildOfflineDescription(card, upgraded);
        var raw = description.GetRawText();
        var result = PlaceholderTokenRegex.Replace(raw, match =>
        {
            var key = match.Groups[1].Value;
            if (!description.Variables.TryGetValue(key, out var value))
                return match.Value;

            return value switch
            {
                LocString locString => locString.GetFormattedText(),
                IConvertible convertible => convertible.ToString(null) ?? match.Value,
                _ => value.ToString() ?? match.Value
            };
        });

        return result;
    }
}
