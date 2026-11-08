using System.Text;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using AiTeammate.AiTeammateCode.Cache;
using AiTeammate.AiTeammateCode.Compat;
using AiTeammate.AiTeammateCode.Settings;

namespace AiTeammate.AiTeammateCode.Decision;

/// <summary>One tool invocation requested by the model. 模型请求的一次工具调用。</summary>
public sealed class ToolCall
{
    public string Name { get; set; } = "";

    public Dictionary<string, string> Args { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string Get(string key) => Args.TryGetValue(key, out var value) ? value : "";
}

/// <summary>
/// Output of one tool round: text for the model plus an optional image attached to the follow-up
/// request (only the screenshot tool produces one). 一次工具调用的结果。
/// </summary>
public sealed class ToolExecutionResult
{
    public string Text { get; init; } = "";

    /// <summary>JPEG data URL of the captured frame, when the model asked for a screenshot.</summary>
    public string? ImageDataUrl { get; init; }
}

/// <summary>
/// Information tools the AI can call before deciding. Everything here reads live combat/run state;
/// the one exception is <c>set_nickname</c>, which changes only the AI's own display name. Must run
/// on the main thread (card text formatting, settings writes).
/// </summary>
public sealed class AiToolRunner
{
    private const int MaxCallsPerRequest = 4;
    private const int MaxResultChars = 4000;

    private static readonly string[] PileKeys = ["hand", "draw", "discard", "exhaust", "deck"];

    private readonly ContextBuilder _context;
    private readonly ContentQueryService _query;

    public AiToolRunner(ContextBuilder context, ContentQueryService query)
    {
        _context = context;
        _query = query;
    }

    /// <summary>Tool manifest for combat decision prompts.</summary>
    public static string DescribeCombatTools(bool includeScreenshot = false) =>
        "- read_pile: args {\"pile\":\"hand|draw|discard|exhaust|deck\", \"detail\":\"brief|full\", \"player\":\"可选，队友名或角色名；省略=自己\"}；返回该玩家该牌堆的全部牌（brief=牌名/费用/类型，full=附带完整描述）。抽牌堆始终不给顺序。\n" +
        "- card_info: args {\"name\":\"卡名\", \"player\":\"可选\"}；返回该卡牌的完整描述（含升级状态；尚未升级的卡附带“升级后”效果）。\n" +
        "- read_relics: args {\"player\":\"可选\"}；返回该玩家全部遗物、层数/计数器与完整效果。\n" +
        "- read_potions: args {\"player\":\"可选\"}；返回该玩家全部药水与完整效果、槽位占用。\n" +
        "- read_player: args {\"player\":\"可选\"}；返回血量/格挡/能量/辉星/金币/状态/手牌/牌堆张数/药水与遗物数量。\n" +
        "- read_run: args {}；返回幕/层/进阶、所有玩家血量与金币、已访问节点统计、构筑判断。\n" +
        DescribeNicknameTool() + "\n" +
        (includeScreenshot ? DescribeScreenshotTool() + "\n" : "") +
        "多人模式下可直接查看队友，例如 {\"name\":\"read_relics\",\"args\":{\"player\":\"队友名\"}}。";

    /// <summary>Tool manifest for out-of-combat decision prompts.</summary>
    public static string DescribeOutOfCombatTools(bool includeScreenshot = false) =>
        "- read_map: args {}；返回整幕地图：当前节点、可前往节点、逐行路线与节点类型。\n" +
        "- read_run: args {}；返回幕/层/进阶、所有玩家血量与金币、已访问节点统计、构筑判断。\n" +
        "- read_player: args {\"player\":\"可选\"}；返回血量/金币/状态/手牌/药水与遗物数量。\n" +
        "- read_relics: args {\"player\":\"可选\"}；返回全部遗物与完整效果。\n" +
        "- read_potions: args {\"player\":\"可选\"}；返回全部药水与完整效果。\n" +
        "- read_pile: args {\"pile\":\"deck|hand\", \"detail\":\"brief|full\", \"player\":\"可选\"}；返回主牌组或手牌内容。\n" +
        "- deck_stats: args {\"player\":\"可选\"}；返回牌组统计（类型/稀有度/平均费用/升级数/关键词）。\n" +
        "- card_info: args {\"name\":\"卡名\", \"player\":\"可选\"}；返回单卡完整描述；尚未升级的卡附带“升级后”效果。\n" +
        DescribeNicknameTool() + "\n" +
        (includeScreenshot ? DescribeScreenshotTool() : "");

    /// <summary>Manifest line for the self-rename tool. 修改自己昵称的工具说明。</summary>
    public static string DescribeNicknameTool() =>
        "- set_nickname: args {\"name\":\"新昵称\"}；修改你自己的显示昵称（最多24字，玩家、聊天与名牌会立刻更新；传空字符串恢复当前人格名）。想改名时随时可调用。";

    /// <summary>Manifest line for the vision tool; only advertised when the setting is on.</summary>
    public static string DescribeScreenshotTool() =>
        "- screenshot: args {}；截取当前游戏画面并以图像形式附在下一条消息里。只有支持图像输入的模型才能使用；如果你的模型不支持视觉，不要调用。";

    /// <summary>Executes the calls and returns text plus an optional screenshot for the model.</summary>
    public ToolExecutionResult Execute(Player aiPlayer, IReadOnlyList<ToolCall> calls)
    {
        var sb = new StringBuilder();
        string? imageDataUrl = null;

        foreach (var call in calls.Take(MaxCallsPerRequest))
        {
            string output;
            try
            {
                if (IsScreenshot(call.Name))
                {
                    output = ExecuteScreenshot(ref imageDataUrl);
                }
                else
                {
                    output = Invoke(aiPlayer, call);
                }
            }
            catch (Exception ex)
            {
                output = $"工具执行失败: {ex.Message}";
            }

            if (sb.Length > 0)
                sb.AppendLine();
            sb.AppendLine($"[{call.Name}]");
            sb.AppendLine(output);
        }

        var text = sb.ToString().TrimEnd();
        if (text.Length > MaxResultChars)
            text = text[..MaxResultChars] + "\n…（结果过长已截断）";

        return new ToolExecutionResult { Text = text, ImageDataUrl = imageDataUrl };
    }

    private static bool IsScreenshot(string name) =>
        name.Trim().ToLowerInvariant() is "screenshot" or "capture_screen" or "see_screen";

    private static string ExecuteScreenshot(ref string? imageDataUrl)
    {
        if (!AiSettingsStore.Current.EnableScreenshotTool)
            return "截图工具未启用：请在设置中打开“允许 AI 查看游戏画面”，并使用支持图像输入的模型。";

        if (imageDataUrl != null)
            return "本轮已经截取过一张画面，不会重复附加图像；请直接基于已有截图判断。";

        var shot = ScreenshotTool.Capture();
        if (!shot.IsSuccess)
            return shot.Error;

        imageDataUrl = shot.DataUrl;
        return $"已截取当前游戏画面（{shot.Width}×{shot.Height}），图像已附在下一条消息中。";
    }

    private string Invoke(Player aiPlayer, ToolCall call)
    {
        switch (call.Name.Trim().ToLowerInvariant())
        {
            case "read_pile":
            case "read_piles":
            {
                var pile = call.Get("pile");
                if (string.IsNullOrWhiteSpace(pile))
                    pile = call.Get("name");

                if (string.IsNullOrWhiteSpace(pile))
                    return $"缺少参数 pile。可用: {string.Join(", ", PileKeys)}";

                if (!TryResolveTarget(aiPlayer, call.Get("player"), out var target, out var error))
                    return error;

                var detail = call.Get("detail");
                var full = detail.Equals("full", StringComparison.OrdinalIgnoreCase)
                           || call.Get("full").Equals("true", StringComparison.OrdinalIgnoreCase);
                return _context.BuildPileContents(aiPlayer, pile, full, target);
            }

            case "card_info":
            case "read_card":
            {
                var name = call.Get("name");
                if (string.IsNullOrWhiteSpace(name))
                    name = call.Get("card_name");
                if (string.IsNullOrWhiteSpace(name))
                    return "缺少参数 name（卡牌名称）。";

                if (!TryResolveTarget(aiPlayer, call.Get("player"), out var target, out var error))
                    return error;

                return BuildCardInfo(target, name.Trim());
            }

            case "read_relics":
            {
                if (!TryResolveTarget(aiPlayer, call.Get("player"), out var target, out var error))
                    return error;
                return _context.DescribeRelics(target);
            }

            case "read_potions":
            {
                if (!TryResolveTarget(aiPlayer, call.Get("player"), out var target, out var error))
                    return error;
                return _context.DescribePotions(target);
            }

            case "read_player":
            {
                if (!TryResolveTarget(aiPlayer, call.Get("player"), out var target, out var error))
                    return error;
                return _context.DescribePlayer(target);
            }

            case "read_run":
                return _context.DescribeRun(aiPlayer);

            case "set_nickname":
            case "rename_self":
            {
                var name = call.Get("name");
                if (string.IsNullOrWhiteSpace(name))
                    name = call.Get("nickname");
                var change = AiNames.SetNickname(name, "ai-tool");
                return change.Message;
            }

            case "read_map":
                return ContextBuilder.DescribeMap(aiPlayer);

            case "deck_stats":
            case "read_deck_stats":
            {
                if (!TryResolveTarget(aiPlayer, call.Get("player"), out var target, out var error))
                    return error;
                return _context.DescribeDeckStats(target);
            }

            default:
                return $"未知工具 \"{call.Name}\"。可用: read_pile, card_info, read_relics, read_potions, read_player, read_run, read_map, deck_stats, set_nickname"
                       + (AiSettingsStore.Current.EnableScreenshotTool ? ", screenshot" : "");
        }
    }

    /// <summary>
    /// Optional <c>player</c> argument: empty/self = the AI itself, otherwise a teammate name.
    /// 多人模式下允许读取队友的牌堆。
    /// </summary>
    private static bool TryResolveTarget(Player aiPlayer, string query, out Player target, out string error)
    {
        target = aiPlayer;
        error = "";

        if (string.IsNullOrWhiteSpace(query))
            return true;

        var resolved = ContextBuilder.ResolvePlayer(aiPlayer, query, out var candidates);
        if (resolved == null)
        {
            var available = ContextBuilder.DescribePlayers(candidates);
            error = string.IsNullOrWhiteSpace(available)
                ? $"未找到玩家 \"{query}\"。"
                : $"未找到玩家 \"{query}\"。可查看: {available}";
            return false;
        }

        target = resolved;
        return true;
    }

    private string BuildCardInfo(Player owner, string name)
    {
        var card = ContextBuilder.EnumerateVisibleCards(owner)
            .FirstOrDefault(c => MatchesCard(c, name));

        if (card != null)
        {
            // Prefer the generated cache entry: it carries the upgraded description and keywords.
            var cacheId = card.CanonicalInstance.Id.Entry;
            string line;
            if (_query.GetCardBrief(cacheId) != null)
            {
                line = _query.LookupAndFormatCard(cacheId, card.IsUpgraded);
            }
            else
            {
                var desc = PromptTextHelper.CleanText(PromptTextHelper.GetCardDescriptionText(card, PileType.Hand));
                var costStr = ContextBuilder.FormatCostCompact(card);
                var upgraded = card.IsUpgraded ? "+" : "";
                line = string.IsNullOrWhiteSpace(desc)
                    ? $"{card.Title}{upgraded}({costStr}{ContextBuilder.CardTypeTag(card.Type)})"
                    : $"{card.Title}{upgraded}({costStr}{ContextBuilder.CardTypeTag(card.Type)}): {desc}";
            }

            // The keyword / hover-tip sidebar (title + description) is the only place mod-added mechanics
            // show up; the cached card text never carries it. 词缀侧边栏补进 card_info。
            var tips = PromptTextHelper.GetHoverTipText(card, 240, line);
            if (!string.IsNullOrWhiteSpace(tips))
                line = $"{line} | 词缀/机制: {tips}";

            // An upgradable card that is not upgraded yet: say what the upgrade actually gives.
            var preview = _query.FormatUpgradePreview(card);
            return string.IsNullOrWhiteSpace(preview) ? line : line + "\n" + preview;
        }

        // Not in any pile/deck: fall back to the content cache (modded cards included).
        var enrichment = _query.EnrichContext(name);
        var byName = _query.FormatUpgradePreviewByName(name);
        var text = string.IsNullOrWhiteSpace(enrichment)
            ? $"未找到卡牌 \"{name}\"：不在手牌/牌堆/主牌组中，资料库也没有记录。"
            : enrichment.Trim();
        return string.IsNullOrWhiteSpace(byName) ? text : text + "\n" + byName;
    }

    private static bool MatchesCard(CardModel card, string name)
    {
        return string.Equals(card.Title, name, StringComparison.OrdinalIgnoreCase)
               || string.Equals(card.Id.Entry, name, StringComparison.OrdinalIgnoreCase);
    }
}
