using System.Text.RegularExpressions;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Runs;

namespace AiTeammate.AiTeammateCode.Decision;

/// <summary>Which side a target is allowed to land on.</summary>
internal enum TargetScope
{
    Any,
    Enemy,
    Ally
}

/// <summary>
/// One target resolution outcome. <see cref="Error"/> set means the caller must NOT fall back to a
/// random or "first" creature — it has to report the reason and drop the action.
/// 目标解析结果：要么拿到具体目标，要么拿到一句要回传给模型/日志的失败原因。
/// </summary>
internal readonly struct TargetResolution
{
    public Creature? Creature { get; private init; }

    /// <summary>Index inside <c>CombatState.Enemies</c>, or -1 when the target is not an enemy.</summary>
    public int Index { get; private init; }

    public string? Error { get; private init; }

    public bool IsError => !string.IsNullOrEmpty(Error);

    public static TargetResolution None => new() { Index = -1 };

    public static TargetResolution Ok(Creature? creature, int index = -1)
        => new() { Creature = creature, Index = index };

    public static TargetResolution Fail(string error)
        => new() { Index = -1, Error = error };
}

/// <summary>
/// The single place that turns a target string into a concrete creature.
///
/// Accepted forms: enemy name, monster id, <c>#3</c>, <c>名字#3</c>, a bare number, <c>名字(12/50)</c>
/// (current HP / max HP hint), self aliases (<c>self</c>/<c>我</c>/…) and player names.
///
/// Why this exists: the old path took <c>FirstOrDefault</c> on a substring match, so two enemies with
/// the same name silently hit whichever stood first in the list — and a string that matched nothing
/// fell through to <c>CardCmd.AutoPlay(null)</c>, which picks a *random* living enemy. Both cases
/// looked successful to the caller. Now duplicates are an explicit "ambiguous" error that names the
/// candidate indices, and nothing ever resolves to a silent random target.
/// 同名敌人不再"取第一个"：要么用索引/血量精确定位，要么明确报歧义。
/// </summary>
internal static class CombatTargetResolver
{
    private static readonly Regex HpHintPattern = new(@"(\d{1,4})\s*/\s*(\d{1,4})", RegexOptions.Compiled);

    public static TargetResolution Resolve(CombatState? state, Player? owner, string? targetText,
        TargetScope scope = TargetScope.Any)
    {
        var text = (targetText ?? "").Trim();
        if (text.Length == 0)
        {
            return scope == TargetScope.Enemy
                ? TargetResolution.Fail("缺少目标：攻击牌/单体药水必须指定敌人，写成 敌人名 或 [#索引]")
                : TargetResolution.None;
        }

        var normalized = Normalize(text);

        if (IsSelfAlias(normalized))
        {
            return owner?.Creature != null
                ? TargetResolution.Ok(owner.Creature)
                : TargetResolution.Fail("找不到自己的角色，无法把目标设为自己");
        }

        if (state != null && IsTeammateAlias(normalized))
        {
            var teammates = state.Players.Where(p => p != owner && p.Creature.IsAlive).ToList();
            if (teammates.Count == 1)
                return TargetResolution.Ok(teammates[0].Creature);
            if (teammates.Count == 0)
                return TargetResolution.Fail("没有存活的队友");
            return TargetResolution.Fail("有多个存活队友，请写玩家名");
        }

        var enemies = state?.Enemies;

        if (TryParseIndex(text, out var index, out var namePart))
        {
            if (scope == TargetScope.Ally)
                return TargetResolution.Fail($"「{text}」是敌人索引；这张牌/药水只能对队友使用");
            if (enemies == null)
                return TargetResolution.Fail("当前不在战斗中，无法按索引选敌人");
            if (index < 0 || index >= enemies.Count)
                return TargetResolution.Fail($"目标索引 #{index} 超出范围（当前 0..{enemies.Count - 1}）");

            var enemy = enemies[index];
            if (!enemy.IsAlive)
                return TargetResolution.Fail($"目标 #{index} 已死亡，请用最新快照里的索引");
            if (namePart.Length > 0 && !MatchesCreatureAlias(enemy, namePart))
                return TargetResolution.Fail(
                    $"#{index} 现在指向「{DescribeAt(enemies, index)}」，与请求的「{namePart}」不符"
                    + "（索引以最新一条快照为准）");
            return TargetResolution.Ok(enemy, index);
        }

        if (enemies != null && scope != TargetScope.Ally)
        {
            var matches = new List<(Creature Creature, int Index)>();
            for (var i = 0; i < enemies.Count; i++)
            {
                var candidate = enemies[i];
                if (MatchesCreatureAlias(candidate, text))
                    matches.Add((candidate, i));
            }

            var alive = matches.Where(m => m.Creature.IsAlive).ToList();

            // "名字(12/50)" is a legitimate way to pick one of two same-named enemies.
            var hpHint = TryParseHpHint(text);
            if (alive.Count > 1 && hpHint is { } hint)
            {
                var byHp = alive
                    .Where(m => m.Creature.CurrentHp == hint.Hp && m.Creature.MaxHp == hint.MaxHp)
                    .ToList();
                if (byHp.Count == 1)
                    return TargetResolution.Ok(byHp[0].Creature, byHp[0].Index);
            }

            if (alive.Count == 1)
                return TargetResolution.Ok(alive[0].Creature, alive[0].Index);
            if (alive.Count > 1)
            {
                return TargetResolution.Fail(
                    $"「{text}」命中 {alive.Count} 个同名目标（{DescribeIndices(alive.Select(m => m.Index))}），"
                    + "请用 [#索引] 或 名字(当前血/上限) 指定");
            }
            if (matches.Count >= 1)
                return TargetResolution.Fail($"目标「{text}」已死亡");
        }

        if (state != null && scope != TargetScope.Enemy)
        {
            var players = state.Players.Where(p => MatchesPlayerAlias(p, owner, text)).ToList();
            var alivePlayers = players.Where(p => p.Creature.IsAlive).ToList();
            if (alivePlayers.Count == 1)
                return TargetResolution.Ok(alivePlayers[0].Creature);
            if (alivePlayers.Count > 1)
                return TargetResolution.Fail($"「{text}」匹配到多个玩家，请写完整玩家名");

            if (normalized is "玩家" or "人类玩家" or "队友" or "player" or "human" or "teammate" or "ally")
            {
                var teammate = state.Players.FirstOrDefault(p => p != owner && p.Creature.IsAlive);
                if (teammate != null)
                    return TargetResolution.Ok(teammate.Creature);
            }
        }

        var available = scope == TargetScope.Ally
            ? ""
            : $" 可用敌人：{DescribeAliveList(state)}";
        return TargetResolution.Fail($"找不到目标「{text}」。{available}");
    }

    /// <summary>Index of the creature inside <c>state.Enemies</c>, or -1.</summary>
    public static int IndexOf(CombatState? state, Creature? creature)
    {
        if (state == null || creature == null)
            return -1;

        for (var i = 0; i < state.Enemies.Count; i++)
        {
            if (ReferenceEquals(state.Enemies[i], creature))
                return i;
        }
        return -1;
    }

    /// <summary>"Name[#2]" for enemies, plain name for anything else. Used by logs, prompts and feedback.</summary>
    public static string Describe(CombatState? state, Creature? creature)
    {
        if (creature == null)
            return "";

        var name = SafeName(creature);
        var index = IndexOf(state, creature);
        return index >= 0 ? $"{name}[#{index}]" : name;
    }

    public static bool HasDuplicateAliveNames(CombatState? state)
    {
        if (state == null)
            return false;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var enemy in state.Enemies)
        {
            if (!enemy.IsAlive)
                continue;
            if (!seen.Add(SafeName(enemy)))
                return true;
        }
        return false;
    }

    public static string DescribeAliveList(CombatState? state, int max = 8)
    {
        if (state == null)
            return "（不在战斗中）";

        var parts = new List<string>();
        for (var i = 0; i < state.Enemies.Count && parts.Count < max; i++)
        {
            var enemy = state.Enemies[i];
            if (enemy.IsAlive)
                parts.Add(DescribeAt(state.Enemies, i));
        }

        if (parts.Count == 0)
            return "（无存活敌人）";

        var text = string.Join(", ", parts);
        var aliveCount = state.Enemies.Count(e => e.IsAlive);
        return aliveCount > max ? text + $" …共 {aliveCount} 个" : text;
    }

    /// <summary>Parses "#3", "名字#3" and a bare "3".</summary>
    public static bool TryParseIndex(string? text, out int index, out string namePart)
    {
        index = -1;
        namePart = "";
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0)
            return false;

        if (trimmed[0] == '#')
        {
            if (int.TryParse(trimmed[1..], out index))
                return true;
            index = -1;
            return false;
        }

        var hash = trimmed.LastIndexOf('#');
        if (hash > 0 && hash < trimmed.Length - 1
            && int.TryParse(trimmed[(hash + 1)..], out index))
        {
            namePart = trimmed[..hash].Trim();
            return true;
        }

        if (int.TryParse(trimmed, out index))
        {
            namePart = "";
            return true;
        }

        index = -1;
        return false;
    }

    /// <summary>Name / monster-id alias match (case- and space-insensitive, both directions).</summary>
    public static bool MatchesCreatureAlias(Creature creature, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return false;

        var name = creature.Name ?? "";
        var id = creature.Monster?.Id.Entry ?? "";
        return MatchesAliasText(name, query) || MatchesAliasText(id, query);
    }

    /// <summary>Looks for a "12/50" current/max HP hint in the requested target text.</summary>
    public static (int Hp, int MaxHp)? TryParseHpHint(string? query)
    {
        if (string.IsNullOrEmpty(query))
            return null;

        var match = HpHintPattern.Match(query);
        if (!match.Success)
            return null;
        if (!int.TryParse(match.Groups[1].Value, out var hp)
            || !int.TryParse(match.Groups[2].Value, out var maxHp))
            return null;
        return (hp, maxHp);
    }

    // ---- Internals -------------------------------------------------------------------------

    private static string DescribeAt(IReadOnlyList<Creature> enemies, int index)
    {
        if (index < 0 || index >= enemies.Count)
            return "?";
        var enemy = enemies[index];
        return $"{SafeName(enemy)}[#{index}] {enemy.CurrentHp}/{enemy.MaxHp}";
    }

    private static string DescribeIndices(IEnumerable<int> indices)
        => string.Join(", ", indices.Select(i => "#" + i));

    private static string SafeName(Creature creature)
    {
        try
        {
            var name = creature.Name;
            if (!string.IsNullOrWhiteSpace(name))
                return name;
        }
        catch
        {
            // Loc may not be ready during early startup; the id is a good enough fallback.
        }
        return creature.Monster?.Id.Entry ?? "?";
    }

    private static bool IsSelfAlias(string normalized) =>
        normalized is "self" or "me" or "myself" or "you" or "我" or "自己" or "你"
            or "我自己" or "你自己" or "ai";

    private static bool IsTeammateAlias(string normalized) =>
        normalized is "玩家" or "人类玩家" or "队友" or "player" or "human" or "teammate" or "ally";

    private static bool MatchesPlayerAlias(Player player, Player? owner, string query)
    {
        var normalized = Normalize(query);
        if (player == owner && !IsSelfAlias(normalized))
            return false;

        var aliases = new[]
        {
            GetPlayerDisplayName(player),
            player.Character?.Id.Entry ?? "",
            $"玩家{(int)(player.NetId & 0xFFFF)}"
        };
        return aliases.Any(alias => MatchesAliasText(alias, query));
    }

    private static bool MatchesAliasText(string? alias, string? query)
    {
        if (string.IsNullOrWhiteSpace(alias) || string.IsNullOrWhiteSpace(query))
            return false;

        var left = Normalize(alias);
        var right = Normalize(query);
        if (left.Length == 0 || right.Length == 0)
            return false;

        return left.Contains(right, StringComparison.OrdinalIgnoreCase)
            || right.Contains(left, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string value)
    {
        var cleaned = PromptTextHelper.CleanText(value ?? "");
        return cleaned.Replace(" ", "", StringComparison.Ordinal)
            .Replace("\r", "", StringComparison.Ordinal)
            .Replace("\n", "", StringComparison.Ordinal)
            .Trim()
            .ToLowerInvariant();
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
        catch
        {
            // Platform lookup is best effort; fall through to character id.
        }

        return player.Character?.Id.Entry ?? $"玩家{(int)(player.NetId & 0xFFFF)}";
    }
}
