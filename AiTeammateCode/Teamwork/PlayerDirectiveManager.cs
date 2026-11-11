using System.Text;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Runs;
using AiTeammate.AiTeammateCode.Decision;

namespace AiTeammate.AiTeammateCode.Teamwork;

public enum PlayerDirectiveType
{
    FocusTarget,
    UsePotionOnTeammate,
    ProtectTeammate,
    SupportTeammate,
    GeneralTactic
}

public sealed class PlayerDirectiveProposal
{
    public PlayerDirectiveType Type { get; init; }
    public bool Accepted { get; init; }
    public string Summary { get; init; } = "";
    public string Target { get; init; } = "";
    public string ItemHint { get; init; } = "";
    public string Reason { get; init; } = "";
    public int DurationTurns { get; init; } = 1;
}

public sealed class PlayerDirective
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public string SourcePlayerName { get; init; } = "";
    public PlayerDirectiveType Type { get; init; }
    public string Summary { get; init; } = "";
    public string Target { get; init; } = "";
    public string ItemHint { get; init; } = "";
    public string Reason { get; init; } = "";
    public int UsesRemaining { get; set; } = 1;
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
}

public sealed class PlayerDirectiveManager
{
    private readonly Lock _sync = new();
    private readonly List<PlayerDirective> _directives = new();

    public void ResetAll()
    {
        lock (_sync)
        {
            _directives.Clear();
        }
    }

    public void ResetCombat() => ResetAll();

    public void RecordAcceptedDirectives(string playerName, IEnumerable<PlayerDirectiveProposal> proposals)
    {
        var accepted = proposals
            .Where(p => p.Accepted && !string.IsNullOrWhiteSpace(p.Summary))
            .ToList();
        if (accepted.Count == 0)
            return;

        lock (_sync)
        {
            foreach (var proposal in accepted)
            {
                _directives.RemoveAll(d =>
                    d.SourcePlayerName.Equals(playerName, StringComparison.OrdinalIgnoreCase)
                    && d.Type == proposal.Type);

                _directives.Add(new PlayerDirective
                {
                    SourcePlayerName = playerName,
                    Type = proposal.Type,
                    Summary = proposal.Summary.Trim(),
                    Target = proposal.Target.Trim(),
                    ItemHint = proposal.ItemHint.Trim(),
                    Reason = proposal.Reason.Trim(),
                    UsesRemaining = Math.Clamp(proposal.DurationTurns, 1, 3)
                });
            }
        }
    }

    public List<PlayerDirective> GetActiveDirectives()
    {
        lock (_sync)
        {
            return _directives.Select(Clone).ToList();
        }
    }

    public string BuildCombatDirectivePrompt(Player aiPlayer)
    {
        var directives = GetActiveDirectives();
        if (directives.Count == 0)
            return "";

        var sb = new StringBuilder();
        sb.AppendLine("[玩家战术指令]");
        foreach (var directive in directives.Take(6))
        {
            var targetPart = string.IsNullOrWhiteSpace(directive.Target) ? "" : $" | 目标:{directive.Target}";
            var itemPart = string.IsNullOrWhiteSpace(directive.ItemHint) ? "" : $" | 物品提示:{directive.ItemHint}";
            var reasonPart = string.IsNullOrWhiteSpace(directive.Reason) ? "" : $" | 接受原因:{directive.Reason}";
            sb.AppendLine($"- {directive.SourcePlayerName} 请求[{DescribeType(directive.Type)}]: {directive.Summary}{targetPart}{itemPart} | 剩余{directive.UsesRemaining}次行动机会{reasonPart}");
        }

        var aliases = BuildTeammateAliasSummary(aiPlayer);
        if (!string.IsNullOrWhiteSpace(aliases))
            sb.AppendLine($"可用玩家目标别名: {aliases}");

        sb.AppendLine("这些是队友的明确请求。若局势允许，应主动把它们落实到 actions / deferred_actions；若不执行，请在 chat_message 中简短说明原因。");
        return sb.ToString().TrimEnd();
    }

    public string BuildChatDirectiveContext()
    {
        var directives = GetActiveDirectives();
        if (directives.Count == 0)
            return "";

        var sb = new StringBuilder();
        sb.AppendLine("[已记录的队友请求]");
        foreach (var directive in directives.TakeLast(4))
            sb.AppendLine($"- {directive.SourcePlayerName}: {directive.Summary} (剩余{directive.UsesRemaining}次)");
        return sb.ToString().TrimEnd();
    }

    public string? GetPreferredFocusTarget()
    {
        return GetActiveDirectives()
            .FirstOrDefault(d => d.Type == PlayerDirectiveType.FocusTarget && !string.IsNullOrWhiteSpace(d.Target))
            ?.Target;
    }

    public DecisionAction? TryBuildPotionAssistFallback(Player aiPlayer)
    {
        var directive = GetActiveDirectives()
            .FirstOrDefault(d => d.Type == PlayerDirectiveType.UsePotionOnTeammate || d.Type == PlayerDirectiveType.SupportTeammate);
        if (directive == null)
            return null;

        var targetName = ResolvePlayerTargetName(aiPlayer, directive.SourcePlayerName, directive.Target);
        if (string.IsNullOrWhiteSpace(targetName))
            return null;

        var potion = ChooseSupportPotion(aiPlayer.Potions, directive.ItemHint);
        if (potion == null)
            return null;

        return new DecisionAction
        {
            Type = "use_potion",
            PotionName = PromptTextHelper.ResolveLocString(potion.Title, potion.Id.Entry),
            Target = targetName,
            Reason = $"执行队友请求: {directive.Summary}"
        };
    }

    public void RegisterExecutedAction(Player aiPlayer, DecisionAction action)
    {
        if (string.IsNullOrWhiteSpace(action.Type))
            return;

        lock (_sync)
        {
            _directives.RemoveAll(d => IsFulfilled(d, aiPlayer, action));
        }
    }

    public void AdvanceAfterAiTurn()
    {
        lock (_sync)
        {
            foreach (var directive in _directives)
                directive.UsesRemaining--;

            _directives.RemoveAll(d => d.UsesRemaining <= 0);
        }
    }

    private static PlayerDirective Clone(PlayerDirective directive)
    {
        return new PlayerDirective
        {
            Id = directive.Id,
            SourcePlayerName = directive.SourcePlayerName,
            Type = directive.Type,
            Summary = directive.Summary,
            Target = directive.Target,
            ItemHint = directive.ItemHint,
            Reason = directive.Reason,
            UsesRemaining = directive.UsesRemaining,
            CreatedAtUtc = directive.CreatedAtUtc
        };
    }

    private static string DescribeType(PlayerDirectiveType type)
    {
        return type switch
        {
            PlayerDirectiveType.FocusTarget => "集火目标",
            PlayerDirectiveType.UsePotionOnTeammate => "给队友用药水",
            PlayerDirectiveType.ProtectTeammate => "保护队友",
            PlayerDirectiveType.SupportTeammate => "支援队友",
            _ => "战术要求"
        };
    }

    private static string BuildTeammateAliasSummary(Player aiPlayer)
    {
        var players = aiPlayer.RunState?.Players
            .Where(p => p != aiPlayer)
            .ToList();
        if (players == null || players.Count == 0)
            return "";

        var aliases = new List<string>();
        foreach (var player in players)
        {
            var displayName = GetPlayerDisplayName(player);
            var characterName = player.Character?.Id.Entry ?? "Unknown";
            aliases.Add($"{displayName}/{characterName}");
        }

        aliases.Add("玩家/我 表示当前发言的人类玩家");
        return string.Join(" | ", aliases.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string? ResolvePlayerTargetName(Player aiPlayer, string sourcePlayerName, string requestedTarget)
    {
        var state = aiPlayer.RunState;
        if (state == null)
            return null;

        var normalized = NormalizeText(string.IsNullOrWhiteSpace(requestedTarget) ? sourcePlayerName : requestedTarget);
        if (normalized.Length == 0)
            normalized = NormalizeText(sourcePlayerName);

        if (IsAiSelfAlias(normalized))
            return GetPlayerDisplayName(aiPlayer);

        var players = state.Players.ToList();
        var explicitPlayer = players.FirstOrDefault(p => MatchesPlayerAlias(p, normalized));
        if (explicitPlayer != null)
            return GetPlayerDisplayName(explicitPlayer);

        if (IsHumanSpeakerAlias(normalized))
        {
            var human = players.FirstOrDefault(p => p != aiPlayer);
            if (human != null)
                return GetPlayerDisplayName(human);
        }

        return null;
    }

    private static PotionModel? ChooseSupportPotion(IEnumerable<PotionModel>? potions, string itemHint)
    {
        var potionList = potions?.ToList();
        if (potionList == null || potionList.Count == 0)
            return null;

        return potionList
            .Where(p => p.TargetType == TargetType.AnyPlayer && PromptTextHelper.CanBeUsedManually(p))
            .Select(p => new
            {
                Potion = p,
                Score = ScoreSupportPotion(p, itemHint)
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Select(x => x.Potion)
            .FirstOrDefault();
    }

    private static int ScoreSupportPotion(PotionModel potion, string itemHint)
    {
        var name = PromptTextHelper.ResolveLocString(potion.Title, potion.Id.Entry);
        var desc = PromptTextHelper.CleanText(PromptTextHelper.ResolveLocString(potion.DynamicDescription, ""));
        var text = $"{name} {desc}";

        var score = 1;
        if (!string.IsNullOrWhiteSpace(itemHint) && text.Contains(itemHint, StringComparison.OrdinalIgnoreCase))
            score += 12;

        if (text.Contains("治疗") || text.Contains("恢复") || text.Contains("heal", StringComparison.OrdinalIgnoreCase))
            score += 8;
        if (text.Contains("格挡") || text.Contains("block", StringComparison.OrdinalIgnoreCase))
            score += 6;
        if (text.Contains("力量") || text.Contains("strength", StringComparison.OrdinalIgnoreCase))
            score += 5;
        if (text.Contains("敏捷") || text.Contains("dexterity", StringComparison.OrdinalIgnoreCase))
            score += 5;
        if (text.Contains("再生") || text.Contains("regen", StringComparison.OrdinalIgnoreCase))
            score += 5;
        if (text.Contains("抽") || text.Contains("能量") || text.Contains("focus", StringComparison.OrdinalIgnoreCase))
            score += 3;

        return score;
    }

    private static bool IsFulfilled(PlayerDirective directive, Player aiPlayer, DecisionAction action)
    {
        return directive.Type switch
        {
            PlayerDirectiveType.FocusTarget =>
                !string.IsNullOrWhiteSpace(action.Target)
                && (action.Type == "play_card" || action.Type == "use_potion")
                && NamesMatch(action.Target, directive.Target),
            PlayerDirectiveType.UsePotionOnTeammate =>
                action.Type == "use_potion"
                && PlayerActionMatchesDirective(aiPlayer, directive, action.Target),
            PlayerDirectiveType.ProtectTeammate =>
                action.Type == "play_card"
                && PlayerActionMatchesDirective(aiPlayer, directive, action.Target),
            PlayerDirectiveType.SupportTeammate =>
                (action.Type == "play_card" || action.Type == "use_potion")
                && PlayerActionMatchesDirective(aiPlayer, directive, action.Target),
            _ => false
        };
    }

    private static bool PlayerActionMatchesDirective(Player aiPlayer, PlayerDirective directive, string actionTarget)
    {
        var expectedTarget = ResolvePlayerTargetName(aiPlayer, directive.SourcePlayerName, directive.Target);
        if (string.IsNullOrWhiteSpace(expectedTarget))
            return false;

        return NamesMatch(expectedTarget, actionTarget);
    }

    private static bool MatchesPlayerAlias(Player player, string normalized)
    {
        var aliases = new[]
        {
            GetPlayerDisplayName(player),
            player.Character?.Id.Entry ?? "",
            $"玩家{(int)(player.NetId & 0xFFFF)}"
        };

        return aliases.Any(alias => NamesMatch(alias, normalized));
    }

    private static bool NamesMatch(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;

        var a = NormalizeText(left);
        var b = NormalizeText(right);
        return a.Contains(b, StringComparison.OrdinalIgnoreCase)
            || b.Contains(a, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAiSelfAlias(string normalized)
    {
        return normalized is "你" or "你自己" or "ai" or "自己" or "self";
    }

    private static bool IsHumanSpeakerAlias(string normalized)
    {
        return normalized is "我" or "我自己" or "玩家" or "人类玩家" or "队友" or "当前玩家";
    }

    private static string NormalizeText(string value)
    {
        var cleaned = PromptTextHelper.CleanText(value ?? "");
        cleaned = cleaned.Replace(" ", "", StringComparison.Ordinal)
            .Replace("\r", "", StringComparison.Ordinal)
            .Replace("\n", "", StringComparison.Ordinal)
            .Trim();
        return cleaned.ToLowerInvariant();
    }

    private static string GetPlayerDisplayName(Player player)
    {
        try
        {
            var platform = RunManager.Instance?.NetService?.Platform;
            if (platform.HasValue)
            {
                var name = PlatformUtil.GetPlayerName(platform.Value, player.NetId);
                if (!string.IsNullOrWhiteSpace(name))
                    return name;
            }
        }
        catch { }

        return player.Character?.Id.Entry ?? $"玩家{(int)(player.NetId & 0xFFFF)}";
    }
}
