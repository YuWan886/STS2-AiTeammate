using System.Text;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Runs;
using AiTeammate.AiTeammateCode;
using AiTeammate.AiTeammateCode.Agent;
using AiTeammate.AiTeammateCode.Cache;
using AiTeammate.AiTeammateCode.Chat;
using AiTeammate.AiTeammateCode.Context;
using AiTeammate.AiTeammateCode.Settings;

namespace AiTeammate.AiTeammateCode.Decision;

public class ContextBuilder
{
    private const int MaxInlinePileEntries = 40;
    private const int MaxToolPileEntries = 80;
    private const int MaxDetailedPileEntries = 30;

    private readonly ContentQueryService _query;

    public ContextBuilder(ContentQueryService query)
    {
        _query = query;
    }

    public PromptSnapshot BuildCombatPromptSnapshot(Player aiPlayer)
    {
        var snapshot = new PromptSnapshot();
        var state = CombatManager.Instance.DebugOnlyGetState();
        if (state == null)
        {
            snapshot.Add("combat_state", "战场", "Combat state unavailable");
            return snapshot;
        }

        snapshot.Add("round", "回合", $"第{state.RoundNumber}回合 | 当前阶段:{state.CurrentSide}");
        snapshot.Add("self", "你", BuildPlayerStateCompact(aiPlayer, includeHand: true));

        var teammateInfos = state.Players
            .Where(p => p != aiPlayer)
            .Select(player =>
            {
                var summary = BuildPlayerStateCompact(player, includeHand: false);
                var hint = BuildCoordinationHintCompact(player);
                return string.IsNullOrWhiteSpace(hint)
                    ? $"{GetCharacterName(player)} {summary}"
                    : $"{GetCharacterName(player)} {summary} | 协同:{hint}";
            })
            .ToList();
        if (teammateInfos.Count > 0)
            snapshot.Add("teammates", "队友", string.Join(" || ", teammateInfos));

        snapshot.Add("enemies", "敌人", BuildEnemyStateLine(state));

        var threat = BuildThreatAssessmentText(state, aiPlayer);
        if (!string.IsNullOrWhiteSpace(threat))
            snapshot.Add("threat", "威胁", threat);

        var potions = BuildPotionSummary(aiPlayer);
        if (!string.IsNullOrWhiteSpace(potions))
            snapshot.Add("potions", "药水", potions, essential: false);

        var piles = BuildPileSummary(aiPlayer);
        if (!string.IsNullOrWhiteSpace(piles))
            snapshot.Add("piles", "牌堆", piles, essential: false);

        if (aiPlayer.PlayerCombatState != null)
        {
            var handKnowledge = _query.FormatCardKnowledge(aiPlayer.PlayerCombatState.Hand.Cards, maxCount: 10);
            if (!string.IsNullOrWhiteSpace(handKnowledge))
                snapshot.Add("hand_knowledge", "手牌资料", handKnowledge, essential: false);
        }

        var potionKnowledge = _query.FormatPotionKnowledge(aiPlayer.Potions ?? Enumerable.Empty<PotionModel>(), maxCount: 4);
        if (!string.IsNullOrWhiteSpace(potionKnowledge))
            snapshot.Add("potion_knowledge", "药水资料", potionKnowledge, essential: false);

        // 遗物一般自动触发，信息过多反而干扰判断，已移除

        return snapshot;
    }

    /// <summary>
    /// 构建 [当前关键状态] 的详细内容。
    /// 包含回合、自身/队友的完整手牌与药水、敌人状态、威胁评估。
    /// </summary>
    public string BuildCombatEssentials(Player aiPlayer)
    {
        var state = CombatManager.Instance.DebugOnlyGetState();
        if (state == null)
            return "Combat state unavailable";

        var sb = new StringBuilder();

        // 回合（不需要当前阶段，因为发请求都在玩家回合）
        sb.AppendLine($"回合: 第{state.RoundNumber}回合");

        // 你自己
        sb.AppendLine(BuildDetailedSelfSection(aiPlayer));

        if (AgentDecisionBroker.Enabled)
        {
            var relicLine = BuildRelicSummary(aiPlayer);
            if (!string.IsNullOrWhiteSpace(relicLine))
                sb.AppendLine($"遗物: {relicLine}");
        }

        // 队友
        foreach (var teammate in state.Players.Where(p => p != aiPlayer))
            sb.AppendLine(BuildDetailedTeammateSection(teammate));

        // 敌人
        var enemyLine = BuildEnemyStateLine(state);
        sb.AppendLine($"敌人: {enemyLine}");

        // 威胁
        var threat = BuildThreatAssessmentText(state, aiPlayer);
        if (!string.IsNullOrWhiteSpace(threat))
            sb.AppendLine($"威胁: {threat}");

        // 牌堆
        var piles = BuildPileDetail(aiPlayer);
        if (!string.IsNullOrWhiteSpace(piles))
        {
            sb.AppendLine("牌堆:");
            sb.AppendLine(piles);
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// The player's own relics with counters and a short effect line. Only used for the external
    /// agent: the built-in prompt intentionally drops relics (they are mostly automatic and used to
    /// crowd a small context), but relic counters are exactly what makes an AI pick the right card
    /// (Pen Nib 9/10, Shuriken/Kunai stacks, Orichalcum, energy relics, ...).
    /// 外部 Agent 模式下给出遗物与计数。
    /// </summary>
    public string BuildRelicSummary(Player player, int maxCount = 20)
    {
        var relics = player.Relics?.ToList();
        if (relics == null || relics.Count == 0)
            return "";

        var parts = new List<string>();
        foreach (var relic in relics.Take(maxCount))
        {
            try
            {
                var name = SafeLocText(relic.Title);
                if (string.IsNullOrWhiteSpace(name))
                    name = relic.Id.Entry;

                var counter = relic.ShowCounter ? $"[{relic.DisplayAmount}]" : "";
                var desc = SafeLocText(relic.DynamicDescription);
                desc = System.Text.RegularExpressions.Regex.Replace(desc, @"\[.*?\]", "");
                desc = PromptTextHelper.CleanText(desc);
                desc = PromptTextHelper.Truncate(desc, 70);

                parts.Add(string.IsNullOrWhiteSpace(desc)
                    ? $"{name}{counter}"
                    : $"{name}{counter}({desc})");
            }
            catch
            {
                parts.Add(relic.Id.Entry);
            }
        }

        if (relics.Count > maxCount)
            parts.Add($"+{relics.Count - maxCount}");

        return string.Join(" | ", parts);
    }

    /// <summary>构建 "你" 的详细段落：基础状态 + 手牌 + 药水</summary>
    private string BuildDetailedSelfSection(Player aiPlayer)
    {
        var sb = new StringBuilder();
        var combat = aiPlayer.PlayerCombatState;
        var creature = aiPlayer.Creature;
        var currentEnergy = (int)(combat?.Energy ?? 0);
        var maxEnergy = (int)(combat?.MaxEnergy ?? 0);
        var currentStars = (int)(combat?.Stars ?? 0);
        var starsInfo = currentStars > 0 ? $" 辉星:{currentStars}" : "";

        // 基础状态行
        sb.AppendLine($"你: HP{creature.CurrentHp}/{creature.MaxHp} 格挡{creature.Block} 能量{currentEnergy}/{maxEnergy}{starsInfo}");

        // 状态/能力
        var powers = creature.Powers;
        if (powers != null && powers.Any())
            sb.AppendLine($"  状态: {string.Join(" | ", powers.Select(FormatPowerBrief))}");

        // 手牌详情
        if (combat != null)
        {
            var handCards = combat.Hand.Cards.ToList();
            if (handCards.Count > 0)
            {
                sb.AppendLine("  手牌:");
                foreach (var card in handCards)
                    sb.AppendLine($"    {FormatCardDetail(card)}");
            }
            else
            {
                sb.AppendLine("  手牌: 空");
            }
        }

        // 药水详情
        AppendDetailedPotions(sb, aiPlayer.Potions);

        return sb.ToString().TrimEnd();
    }

    /// <summary>构建队友的详细段落：玩家名称-角色 | 基础状态 + 手牌 + 药水</summary>
    private static string BuildDetailedTeammateSection(Player teammate)
    {
        var sb = new StringBuilder();
        var combat = teammate.PlayerCombatState;
        var creature = teammate.Creature;
        var charName = GetCharacterName(teammate);
        var starsInfo = combat?.Stars > 0 ? $" 辉星:{combat.Stars}" : "";

        // 获取玩家显示名称
        var playerName = GetPlayerDisplayName(teammate);

        // 基础状态行（玩家名 - 角色 | 状态）
        sb.AppendLine($"队友: {playerName} - {charName} | {creature.CurrentHp}/{creature.MaxHp}HP 格挡{creature.Block} 能量{combat?.Energy}/{combat?.MaxEnergy}{starsInfo}");

        // 状态/能力
        var powers = creature.Powers;
        if (powers != null && powers.Any())
            sb.AppendLine($"  状态: {string.Join(" | ", powers.Select(FormatPowerBrief))}");

        // 手牌详情
        if (combat != null)
        {
            var handCards = combat.Hand.Cards.ToList();
            if (handCards.Count > 0)
            {
                sb.AppendLine("  手牌:");
                foreach (var card in handCards)
                    sb.AppendLine($"    {FormatCardDetail(card)}");
            }
            else
            {
                sb.AppendLine("  手牌: 空");
            }

            // Contents are intentionally not inlined for teammates; the AI can ask via read_pile.
            sb.AppendLine($"  牌堆: {FormatPileCounts(combat)}");
        }

        // 药水详情
        AppendDetailedPotions(sb, teammate.Potions);

        return sb.ToString().TrimEnd();
    }

    /// <summary>格式化单张卡牌的详细信息：名称 | 费用 | 目标 | 效果</summary>
    private static string FormatCardDetail(CardModel card)
    {
        var name = card.Title ?? card.Id.Entry;
        var costStr = FormatCostCompact(card);

        // 目标类型
        var targetStr = card.TargetType.ToString();

        // 效果描述
        var desc = PromptTextHelper.ResolveCardDescription(card, PileType.Hand, card.Id.Entry);
        // 去除 BBCode 标签
        desc = System.Text.RegularExpressions.Regex.Replace(desc, @"\[.*?\]", "");
        desc = desc.Trim();
        if (desc.Length > 120) desc = desc[..120] + "...";

        return $"{name} | {costStr} | 目标:{targetStr} | 效果:{desc}";
    }

    /// <summary>追加药水详细信息到 StringBuilder</summary>
    private static void AppendDetailedPotions(StringBuilder sb, IEnumerable<PotionModel>? potions)
    {
        var potionList = potions?.ToList();
        if (potionList == null || potionList.Count == 0)
        {
            sb.AppendLine("  药水: 无");
            return;
        }

        sb.AppendLine("  药水:");
        foreach (var potion in potionList)
        {
            var name = SafeLocText(potion.Title);
            var desc = SafeLocText(potion.DynamicDescription);
            desc = System.Text.RegularExpressions.Regex.Replace(desc, @"\[.*?\]", "");
            desc = desc.Trim();
            if (desc.Length > 80) desc = desc[..80] + "...";

            if (string.IsNullOrEmpty(desc))
                sb.AppendLine($"    {name}");
            else
                sb.AppendLine($"    {name} | 效果:{desc}");
        }
    }


    /// <summary>Compact player state — one line for stats and important powers.</summary>
    private string BuildPlayerStateCompact(Player player, bool includeHand)
    {
        var combat = player.PlayerCombatState;
        var creature = player.Creature;
        var starsInfo = combat?.Stars > 0 ? $" 辉星:{combat.Stars}" : "";

        var sb = new StringBuilder();
        sb.Append($"{creature.CurrentHp}/{creature.MaxHp}HP 格挡{creature.Block} 能量{combat?.Energy}/{combat?.MaxEnergy}{starsInfo}");

        var powers = creature.Powers;
        if (powers != null && powers.Any())
            sb.Append($" | 状态:{string.Join(" ", powers.Select(FormatPowerBrief))}");

        if (combat != null)
            sb.Append($" | 牌堆:{FormatPileCounts(combat)}");

        if (includeHand && combat != null)
        {
            var hand = BuildHandSummary(combat.Hand.Cards.ToList());
            if (!string.IsNullOrWhiteSpace(hand))
                sb.Append($" | 手牌:{hand}");
        }

        return sb.ToString();
    }

    private static string GetCardEffectHint(CardModel card)
    {
        try
        {
            var desc = PromptTextHelper.GetCardDescriptionText(card, PileType.Hand);
            if (string.IsNullOrWhiteSpace(desc)) return "";

            // Strip BBCode tags
            desc = System.Text.RegularExpressions.Regex.Replace(desc, @"\[.*?\]", "");

            // Extract key numbers for common effects (Chinese + English game text)
            var parts = new List<string>();
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(desc, @"造成(\d+)点伤害"))
                parts.Add($"伤{m.Groups[1].Value}");
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(desc, @"获得(\d+)点格挡"))
                parts.Add($"甲{m.Groups[1].Value}");
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(desc, @"给予(\d+)层"))
                parts.Add($"层{m.Groups[1].Value}");
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(desc, @"[Dd]eal (\d+) damage"))
                parts.Add($"伤{m.Groups[1].Value}");
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(desc, @"[Gg]ain (\d+) (?:[Bb]lock)"))
                parts.Add($"甲{m.Groups[1].Value}");
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(desc, @"[Aa]pply (\d+)"))
                parts.Add($"层{m.Groups[1].Value}");

            // If no specific numbers found, check for keywords
            if (parts.Count == 0)
            {
                if (desc.Contains("易伤") || desc.Contains("Vulnerable")) parts.Add("易伤");
                if (desc.Contains("虚弱") || desc.Contains("Weak")) parts.Add("虚弱");
                if (desc.Contains("力量") || desc.Contains("Strength")) parts.Add("力量");
                if (desc.Contains("AOE") || desc.Contains("所有敌人") || desc.Contains("ALL enemies")) parts.Add("AOE");
                if (desc.Contains("抽牌") || desc.Contains("抽") || desc.Contains("Draw")) parts.Add("抽");
                if (desc.Contains("辉星") || desc.Contains("Star")) parts.Add("星");
            }

            return parts.Count > 0 ? $":{string.Join("/", parts.Take(3))}" : "";
        }
        catch { return ""; }
    }

    private string BuildCoordinationHintCompact(Player teammate)
    {
        var c = teammate.PlayerCombatState;
        var state = CombatManager.Instance.DebugOnlyGetState();
        if (c == null) return "";
        var hand = c.Hand.Cards.ToList();
        if (hand.Count == 0) return "";

        var hints = new List<string>();
        foreach (var card in hand)
        {
            var title = card.Title ?? card.Id.Entry;
            try
            {
                var desc = PromptTextHelper.GetCardDescriptionText(card, PileType.Hand);
                if (string.IsNullOrWhiteSpace(desc))
                    continue;

                if (desc.Contains("易伤") || desc.Contains("破甲") || desc.Contains("虚弱"))
                    hints.Add($"{title}(上减益)");
                if ((desc.Contains("力量") || desc.Contains("敏捷") || desc.Contains("缓冲")) && card.Type != CardType.Attack)
                    hints.Add($"{title}(增益)");
                if (desc.Contains("所有敌人") || desc.Contains("每个敌人"))
                    hints.Add($"{title}(AOE)");
                if (desc.Contains("格挡") && card.Type == CardType.Skill)
                    hints.Add($"{title}(格挡)");

                if (card.Type == CardType.Attack && state != null)
                {
                    var damageMatch = System.Text.RegularExpressions.Regex.Match(desc, @"造成(\d+)点伤害");
                    if (damageMatch.Success && int.TryParse(damageMatch.Groups[1].Value, out var damage))
                    {
                        var killTarget = state.Enemies
                            .Where(e => e.IsAlive && e.CurrentHp <= damage + 2)
                            .OrderBy(e => e.CurrentHp)
                            .FirstOrDefault();
                        if (killTarget != null)
                            hints.Add($"{title}(可补刀{killTarget.Name})");
                    }
                }
            }
            catch { }
        }
        return hints.Count > 0 ? string.Join(", ", hints.Distinct().Take(4)) : "";
    }

    /// <summary>Format a power as "name:description(amount)". Falls back to name-only if description has unresolved placeholders.</summary>
    private static string FormatPowerBrief(MegaCrit.Sts2.Core.Models.PowerModel p)
    {
        var placeholderRe = new System.Text.RegularExpressions.Regex(@"\{[^}]+\}", System.Text.RegularExpressions.RegexOptions.Compiled);

        var name = SafeLocText(p.Title);
        if (string.IsNullOrEmpty(name) || name == p.Id.Entry)
            name = p.Id.Entry.Replace("_POWER", "").Replace("_", " ").ToLowerInvariant();

        // Try Description first (simpler, fewer SmartFormat conditionals)
        var desc = SafeLocText(p.Description);
        desc = System.Text.RegularExpressions.Regex.Replace(desc, @"\[.*?\]", "");
        desc = System.Text.RegularExpressions.Regex.Replace(desc, @"res://\S+", "").Trim();

        // If unresolved, try SmartDescription
        if (placeholderRe.IsMatch(desc) && p.HasSmartDescription)
        {
            var smartDesc = SafeLocText(p.SmartDescription);
            smartDesc = System.Text.RegularExpressions.Regex.Replace(smartDesc, @"\[.*?\]", "");
            smartDesc = System.Text.RegularExpressions.Regex.Replace(smartDesc, @"res://\S+", "").Trim();
            if (!placeholderRe.IsMatch(smartDesc))
                desc = smartDesc;
        }

        // If STILL has unresolved placeholders, skip description
        if (placeholderRe.IsMatch(desc))
            desc = "";

        if (desc.Length > 30) desc = desc[..30] + "...";

        var amountStr = p.Amount != 1 ? $"({p.Amount})" : "";
        return string.IsNullOrEmpty(desc) ? $"{name}{amountStr}" : $"{name}:{desc}{amountStr}";
    }

    internal static string FormatIntentCompact(MoveState move, Creature? owner = null)
    {
        var parts = new List<string>();
        var attackIntents = move.Intents.OfType<AttackIntent>().ToList();
        if (attackIntents.Count > 0)
        {
            var damageParts = new List<string>();
            try
            {
                var state = CombatManager.Instance.DebugOnlyGetState();
                var players = state?.Players.Select(p => p.Creature).ToList() ?? new List<Creature>();
                // The owner matters: strength/weak on THIS monster change the damage, so reading
                // another enemy's stats (the old code always used the first enemy) was wrong.
                var actualOwner = owner ?? state?.Enemies.FirstOrDefault(e => e.IsAlive);
                foreach (var attack in attackIntents)
                {
                    if (actualOwner == null)
                    {
                        damageParts.Add("?");
                        continue;
                    }

                    var single = attack.GetSingleDamage(players, actualOwner);
                    // Multi-hit moves report hit count through Repeats (the UI shows "12×3").
                    var repeats = attack.Repeats;
                    damageParts.Add(repeats > 1 ? $"{single}×{repeats}" : single.ToString());
                }
            }
            catch { damageParts.Add("?"); }

            parts.Add(damageParts.Count > 0 ? $"伤害{string.Join("/", damageParts)}" : $"攻击×{attackIntents.Count}");
        }

        foreach (var intent in move.Intents.Where(candidate => candidate is not AttackIntent))
        {
            parts.Add(intent.IntentType switch
            {
                IntentType.Defend => "防御",
                IntentType.Buff => "增益",
                IntentType.Debuff => "减益",
                IntentType.DebuffStrong => "强减益",
                IntentType.StatusCard => intent is StatusIntent status ? $"塞牌×{status.CardCount}" : "塞牌",
                IntentType.CardDebuff => "卡牌弱化",
                IntentType.Summon => "召唤",
                IntentType.Heal => "回血",
                IntentType.Escape => "逃跑",
                IntentType.Sleep => "睡眠",
                IntentType.Stun => "眩晕",
                IntentType.DeathBlow => "处决",
                IntentType.Hidden => "隐藏",
                IntentType.Unknown => "未知",
                _ => intent.IntentType.ToString()
            });
        }

        return parts.Count > 0 ? string.Join(",", parts) : "无";
    }

    private string FormatCardBrief(CardModel card)
    {
        var name = card.Title ?? card.Id.Entry;
        var oneLiner = GetCardOneLiner(card, 80);

        return $"{name}({FormatCostCompact(card)}) {oneLiner}";
    }

    private static string GetCharacterName(Player player)
    {
        return player.Character?.Id.Entry ?? "Unknown";
    }

        // ---- Public helpers for DecisionService ----

    public string EnrichContext(string text) => _query.EnrichContext(text);

    private string BuildEnemyStateLine(CombatState state)
    {
        var enemyInfos = new List<string>();
        // The [#n] prefix is the index inside CombatState.Enemies — the same number the bridge
        // snapshot publishes and the number targets accept as "#n". It is the ONLY way to address
        // one of two same-named enemies, so the prompt has to show it. 敌人行带索引，同名敌人只能靠它区分。
        for (var i = 0; i < state.Enemies.Count; i++)
        {
            var enemy = state.Enemies[i];
            if (!enemy.IsAlive)
                continue;

            var intent = enemy.Monster?.NextMove != null
                ? FormatIntentCompact(enemy.Monster.NextMove, enemy)
                : "未知";
            // Armor decides whether an attack actually lands, so it belongs next to the HP.
            // 敌人的护甲和状态都要给，不能只给玩家。
            var powers = FormatEnemyPowers(enemy);
            enemyInfos.Add($"[#{i}] {enemy.Name}({enemy.CurrentHp}/{enemy.MaxHp} 格挡{enemy.Block}) {intent}{powers}");
        }
        return enemyInfos.Count > 0 ? string.Join(" | ", enemyInfos) : "无存活敌人";
    }

    /// <summary>
    /// Enemy buffs/debuffs with stacks. Vulnerable/Weak get their short Chinese tag, everything else
    /// reuses <see cref="FormatPowerBrief"/>; negative stacks count too (a Strength-down, ...).
    /// 敌人身上的状态与层数。
    /// </summary>
    private static string FormatEnemyPowers(Creature enemy)
    {
        if (enemy.Powers == null || enemy.Powers.Count == 0) return "";

        var parts = new List<string>();
        foreach (var power in enemy.Powers)
        {
            if (power.Amount == 0) continue;

            var id = power.Id.Entry;
            // Power ids are the class name uppercased ("VulnerablePower" -> "VULNERABLE_POWER"),
            // so strip the suffix before matching the two common debuffs.
            var key = id.EndsWith("_POWER", StringComparison.OrdinalIgnoreCase)
                ? id[..^"_POWER".Length]
                : id;
            if (key.Equals("Vulnerable", StringComparison.OrdinalIgnoreCase))
                parts.Add($"[易伤×{power.Amount}]");
            else if (key.Equals("Weak", StringComparison.OrdinalIgnoreCase))
                parts.Add($"[虚弱×{power.Amount}]");
            else
                parts.Add($"[{FormatPowerBrief(power)}]");
        }

        return parts.Count > 0 ? " " + string.Join("", parts) : "";
    }

    private string BuildHandSummary(List<CardModel> handCards)
    {
        if (handCards.Count == 0)
            return "空";

        return string.Join(", ", handCards.Select(card =>
        {
            var name = card.Title ?? card.Id.Entry;
            var costStr = FormatCostCompact(card);
            var typeTag = card.Type switch { CardType.Attack => "攻", CardType.Skill => "技", CardType.Power => "能", _ => "" };
            var hint = GetCardEffectHint(card);
            return $"[{name}({costStr}){typeTag}{hint}]";
        }));
    }

    private string BuildPotionSummary(Player aiPlayer)
    {
        var potions = aiPlayer.Potions;
        if (potions == null || !potions.Any())
            return "";

        var potionInfos = potions.Select(p =>
        {
            var name = SafeLocText(p.Title);
            var desc = SafeLocText(p.DynamicDescription);
            if (desc.Length > 40) desc = desc[..40] + "...";
            return string.IsNullOrEmpty(desc) ? name : $"{name}:{desc}";
        });
        return string.Join(" | ", potionInfos);
    }

    private string BuildPileSummary(Player aiPlayer)
    {
        var combat = aiPlayer.PlayerCombatState;
        if (combat == null)
            return "";

        var drawCount = combat.DrawPile.Cards.Count;
        var discardCount = combat.DiscardPile.Cards.Count;
        var exhaustCount = combat.ExhaustPile.Cards.Count;
        return exhaustCount > 0
            ? $"抽牌堆:{drawCount}张 弃牌堆:{discardCount}张 消耗堆:{exhaustCount}张"
            : $"抽牌堆:{drawCount}张 弃牌堆:{discardCount}张";
    }

    /// <summary>
    /// Draw / discard / exhaust pile contents for combat prompts. Identical cards are merged with ×N
    /// and the draw pile order is deliberately omitted — a human cannot see it either. Falls back to
    /// the pile counts when <see cref="AiSettings.EnablePileDetail"/> is off (small-context models).
    /// </summary>
    public string BuildPileDetail(Player aiPlayer)
    {
        if (!AiSettingsStore.Current.EnablePileDetail)
            return BuildPileSummary(aiPlayer);

        var combat = aiPlayer.PlayerCombatState;
        if (combat == null)
            return "";

        var lines = new List<string>
        {
            FormatPile("抽牌堆", combat.DrawPile.Cards, PileType.Draw,
                MaxInlinePileEntries, fullDetail: false, unknownOrder: true),
            FormatPile("弃牌堆", combat.DiscardPile.Cards, PileType.Discard,
                MaxInlinePileEntries, fullDetail: false, unknownOrder: false)
        };
        if (combat.ExhaustPile.Cards.Count > 0)
        {
            lines.Add(FormatPile("消耗堆", combat.ExhaustPile.Cards, PileType.Exhaust,
                MaxInlinePileEntries, fullDetail: false, unknownOrder: false));
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Contents of one pile, used by the <c>read_pile</c> tool. Never exposes the draw-pile order.
    /// Pass <paramref name="target"/> to read a teammate's pile in multiplayer.
    /// </summary>
    /// <param name="aiPlayer">The AI player (prompt owner). 发起查询的 AI。</param>
    /// <param name="pile">hand / draw / discard / exhaust / deck</param>
    /// <param name="fullDetail">Also include each card's description (main-thread formatting).</param>
    /// <param name="target">Whose pile to read; null = the AI's own. 要查看的玩家。</param>
    public string BuildPileContents(Player aiPlayer, string pile, bool fullDetail, Player? target = null)
    {
        var owner = target ?? aiPlayer;
        var combat = owner.PlayerCombatState;
        var key = (pile ?? "").Trim().ToLowerInvariant();

        IEnumerable<CardModel> cards;
        PileType pileType;
        string label;
        var unknownOrder = false;

        switch (key)
        {
            case "hand":
                if (combat == null) return $"{GetPlayerDisplayName(owner)}: 不在战斗中";
                cards = combat.Hand.Cards;
                pileType = PileType.Hand;
                label = "手牌";
                break;
            case "draw":
                if (combat == null) return $"{GetPlayerDisplayName(owner)}: 不在战斗中";
                cards = combat.DrawPile.Cards;
                pileType = PileType.Draw;
                label = "抽牌堆";
                unknownOrder = true;
                break;
            case "discard":
                if (combat == null) return $"{GetPlayerDisplayName(owner)}: 不在战斗中";
                cards = combat.DiscardPile.Cards;
                pileType = PileType.Discard;
                label = "弃牌堆";
                break;
            case "exhaust":
                if (combat == null) return $"{GetPlayerDisplayName(owner)}: 不在战斗中";
                cards = combat.ExhaustPile.Cards;
                pileType = PileType.Exhaust;
                label = "消耗堆";
                break;
            case "deck":
                cards = owner.Deck?.Cards ?? Enumerable.Empty<CardModel>();
                pileType = PileType.Deck;
                label = "主牌组";
                break;
            default:
                return $"未知牌堆 \"{pile}\"。可用: hand, draw, discard, exhaust, deck";
        }

        var maxEntries = fullDetail ? MaxDetailedPileEntries : MaxToolPileEntries;
        var prefix = ReferenceEquals(owner, aiPlayer) ? "" : $"{GetPlayerDisplayName(owner)}的";
        return FormatPile(prefix + label, cards, pileType, maxEntries, fullDetail, unknownOrder);
    }

    /// <summary>
    /// Resolves a player for the read_pile / card_info tools. Accepts self aliases, display name,
    /// character id, NetId, a 1-based index, or a unique partial name. Returns null when ambiguous.
    /// </summary>
    public static Player? ResolvePlayer(Player requester, string query, out IReadOnlyList<Player> candidates)
    {
        IReadOnlyList<Player> players;
        var state = CombatManager.Instance.DebugOnlyGetState();
        if (state != null)
            players = state.Players;
        else
            players = requester.RunState?.Players ?? Array.Empty<Player>();

        candidates = players;

        var q = (query ?? "").Trim();
        if (q.Length == 0)
            return requester;

        if (q is "self" or "me" or "自己" or "我" or "本地玩家")
            return requester;

        foreach (var player in players)
        {
            if (MatchesPlayer(player, q))
                return player;
        }

        if (int.TryParse(q, out var index) && index >= 1 && index <= players.Count)
            return players[index - 1];

        var loose = players.Where(player => LooseMatchesPlayer(player, q)).ToList();
        return loose.Count == 1 ? loose[0] : null;
    }

    /// <summary>Comma-separated player list for tool error messages. 工具报错时列出可查看的玩家。</summary>
    public static string DescribePlayers(IReadOnlyList<Player> players) =>
        string.Join("、", players.Select(p => $"{GetPlayerDisplayName(p)}({GetCharacterName(p)})"));

    private static bool MatchesPlayer(Player player, string query)
    {
        return string.Equals(GetPlayerDisplayName(player), query, StringComparison.OrdinalIgnoreCase)
               || string.Equals(GetCharacterName(player), query, StringComparison.OrdinalIgnoreCase)
               || string.Equals(player.Character?.Id.Entry, query, StringComparison.OrdinalIgnoreCase)
               || string.Equals(player.NetId.ToString(), query, StringComparison.Ordinal)
               || string.Equals((player.NetId & 0xFFFF).ToString(), query, StringComparison.Ordinal)
               || string.Equals(player.NetId.ToString("X"), query, StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooseMatchesPlayer(Player player, string query)
    {
        if (query.Length < 2)
            return false;

        return GetPlayerDisplayName(player).Contains(query, StringComparison.OrdinalIgnoreCase)
               || GetCharacterName(player).Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Pile counts only (teammates' pile contents stay behind the read_pile tool).</summary>
    private static string FormatPileCounts(PlayerCombatState combat)
    {
        var exhaust = combat.ExhaustPile.Cards.Count;
        return exhaust > 0
            ? $"抽牌堆{combat.DrawPile.Cards.Count}张 弃牌堆{combat.DiscardPile.Cards.Count}张 消耗堆{exhaust}张"
            : $"抽牌堆{combat.DrawPile.Cards.Count}张 弃牌堆{combat.DiscardPile.Cards.Count}张";
    }

    // ---- Read-only tool views (also used by prompt builders) ----

    /// <summary>Full status of one player: HP, gold, powers, hand, pile counts, potions, relics.</summary>
    public string DescribePlayer(Player target)
    {
        var creature = target.Creature;
        var combat = target.PlayerCombatState;
        var sb = new StringBuilder();
        sb.Append($"{GetPlayerDisplayName(target)} - {GetCharacterName(target)}");

        if (creature != null)
        {
            var pct = creature.MaxHp > 0 ? creature.CurrentHp * 100 / creature.MaxHp : 0;
            sb.Append($" | HP{creature.CurrentHp}/{creature.MaxHp}({pct}%) 格挡{creature.Block}");
        }

        if (combat != null)
        {
            var stars = combat.Stars > 0 ? $" 辉星{combat.Stars}" : "";
            sb.Append($" 能量{combat.Energy}/{combat.MaxEnergy}{stars} 第{combat.TurnNumber}回合");
        }

        sb.Append($" | 金币{target.Gold}");

        var powers = creature?.Powers;
        if (powers != null && powers.Any())
        {
            sb.AppendLine();
            sb.Append($"状态: {string.Join(" | ", powers.Select(FormatPowerBrief))}");
        }

        if (combat != null)
        {
            var hand = combat.Hand.Cards.Select(card => card.Title ?? card.Id.Entry).ToList();
            var handText = hand.Count > 0 ? string.Join(", ", hand) : "空";
            sb.AppendLine();
            sb.Append($"手牌({hand.Count}): {handText}");
            sb.AppendLine();
            sb.Append($"牌堆: {FormatPileCounts(combat)}");
        }

        var potions = target.Potions?.ToList() ?? new List<PotionModel>();
        var relics = target.Relics?.ToList() ?? new List<RelicModel>();
        sb.AppendLine();
        sb.Append($"药水{potions.Count}/{target.MaxPotionCount} 遗物{relics.Count}");

        return sb.ToString().TrimEnd();
    }

    /// <summary>Every relic of one player with counters and effect text.</summary>
    public string DescribeRelics(Player target)
    {
        var relics = target.Relics?.ToList() ?? new List<RelicModel>();
        var who = GetPlayerDisplayName(target);
        if (relics.Count == 0)
            return $"{who}: 没有遗物";

        var sb = new StringBuilder();
        sb.AppendLine($"{who} 的遗物({relics.Count}):");
        foreach (var relic in relics)
        {
            try
            {
                var name = SafeLocText(relic.Title);
                var markers = new StringBuilder();
                if (relic.StackCount > 1)
                    markers.Append($"×{relic.StackCount}");
                if (relic.ShowCounter)
                    markers.Append($"[{relic.DisplayAmount}]");

                var desc = PromptTextHelper.CleanText(SafeLocText(relic.DynamicDescription));
                if (string.IsNullOrWhiteSpace(desc))
                    desc = _query.GetRelicFull(relic.CanonicalInstance.Id.Entry)?.Description ?? "";
                desc = PromptTextHelper.Truncate(PromptTextHelper.CleanText(desc), 160);

                sb.AppendLine(string.IsNullOrWhiteSpace(desc)
                    ? $"- {name}{markers}"
                    : $"- {name}{markers}: {desc}");
            }
            catch
            {
                sb.AppendLine($"- {relic.Id.Entry}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>Every potion of one player with effect text.</summary>
    public string DescribePotions(Player target)
    {
        var potions = target.Potions?.ToList() ?? new List<PotionModel>();
        var who = GetPlayerDisplayName(target);
        var slots = $"{potions.Count}/{target.MaxPotionCount}";
        if (potions.Count == 0)
            return $"{who}: 没有药水（槽位 {slots}）";

        var sb = new StringBuilder($"{who} 的药水({slots}):");
        foreach (var potion in potions)
        {
            var name = SafeLocText(potion.Title);
            var desc = PromptTextHelper.CleanText(SafeLocText(potion.DynamicDescription));
            desc = PromptTextHelper.Truncate(desc, 160);
            sb.AppendLine(string.IsNullOrWhiteSpace(desc) ? $"- {name}" : $"- {name}: {desc}");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>Run overview: act/floor/ascension, every player's HP and gold, current node, visit counts.</summary>
    public string DescribeRun(Player aiPlayer)
    {
        var runState = aiPlayer.RunState;
        if (runState == null)
            return "当前没有进行中的对局";

        var sb = new StringBuilder();
        var ascension = runState.AscensionLevel > 0 ? $" 进阶{runState.AscensionLevel}" : "";
        sb.AppendLine($"第{runState.CurrentActIndex + 1}幕 第{runState.TotalFloor}层{ascension} | 金币{aiPlayer.Gold}");

        foreach (var player in runState.Players)
        {
            var creature = player.Creature;
            var hp = creature != null ? $"HP{creature.CurrentHp}/{creature.MaxHp}" : "HP?";
            var who = ReferenceEquals(player, aiPlayer) ? "你" : GetPlayerDisplayName(player);
            var inCombat = player.PlayerCombatState != null ? " [战斗中]" : "";
            sb.AppendLine($"{who}({GetCharacterName(player)}) {hp} 金币{player.Gold}{inCombat}");
        }

        if (runState.CurrentMapPoint is { } current)
            sb.AppendLine($"当前位置: {DescribeMapPoint(current)}");

        var visited = runState.MapPointHistory
            .SelectMany(rows => rows)
            .GroupBy(entry => entry.MapPointType)
            .OrderByDescending(group => group.Count())
            .Select(group => $"{MapPointTypeLabel(group.Key)}×{group.Count()}");
        var visitedText = string.Join(" ", visited);
        if (!string.IsNullOrWhiteSpace(visitedText))
            sb.AppendLine($"已访问节点: {visitedText}");

        var strategy = BuildRunStrategyLabel(aiPlayer);
        if (!string.IsNullOrWhiteSpace(strategy))
            sb.AppendLine($"构筑判断: {strategy}");

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Whole act map: current node, reachable nodes and the row-by-row edge graph. Static and
    /// stateless so the dsh bridge can reuse the exact same view for its map phase.
    /// </summary>
    public static string DescribeMap(Player aiPlayer)
    {
        var runState = aiPlayer.RunState;
        if (runState?.Map == null)
            return "地图不可用";

        var sb = new StringBuilder();
        sb.AppendLine($"第{runState.CurrentActIndex + 1}幕地图 | 已访问{GetVisitedMapCoords().Count}个节点");

        var current = runState.CurrentMapPoint;
        sb.AppendLine(current != null ? $"当前位置: {DescribeMapPoint(current)}" : "当前位置: 起点（尚未进入地图）");

        var travelable = GetTravelableMapPoints(aiPlayer);
        if (travelable.Count > 0)
            sb.AppendLine($"可前往: {string.Join("、", travelable.Select(DescribeMapPoint))}");
        if (travelable.Count == 1 && travelable[0].PointType == MapPointType.Ancient)
            sb.AppendLine("（幕开局的先古房间：必须先进入它，本幕下一行的房间才会开放。）");

        sb.AppendLine(runState.Map.SecondBossMapPoint is { } secondBoss
            ? $"Boss: {DescribeMapPoint(runState.Map.BossMapPoint)} | 第二Boss: {DescribeMapPoint(secondBoss)}"
            : $"Boss: {DescribeMapPoint(runState.Map.BossMapPoint)}");

        var visited = new HashSet<MapCoord>(GetVisitedMapCoords());
        sb.AppendLine("路线图（行: 节点[类型] -> 后续节点坐标）:");
        foreach (var row in runState.Map.GetAllMapPoints()
                     .GroupBy(point => point.coord.row)
                     .OrderBy(group => group.Key))
        {
            var parts = row
                .OrderBy(point => point.coord.col)
                .Select(point =>
                {
                    var mark = visited.Contains(point.coord) ? "√" : "";
                    var children = point.Children.Count > 0
                        ? "->" + string.Join(",", point.Children
                            .OrderBy(child => child.coord.col)
                            .Select(child => $"({child.coord.col},{child.coord.row})"))
                        : "";
                    return $"({point.coord.col},{point.coord.row}){mark}{MapPointTypeLabel(point.PointType)}{children}";
                });

            sb.Append($"第{row.Key}行: {string.Join(" ", parts)}");
            if (sb.Length > 3200)
            {
                sb.Append(" …");
                break;
            }
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>Deck composition stats instead of the full card list.</summary>
    public string DescribeDeckStats(Player target)
    {
        var deck = target.Deck?.Cards;
        var who = GetPlayerDisplayName(target);
        if (deck == null || deck.Count == 0)
            return $"{who}: 牌组为空";

        var descriptions = deck
            .Select(card => PromptTextHelper.CleanText(
                PromptTextHelper.ResolveCardDescription(card, PileType.Hand, card.Id.Entry)))
            .ToList();

        var averageCost = deck.Average(card => (double)PromptTextHelper.ResolveEnergyCost(card));
        var upgraded = deck.Count(card => card.IsUpgraded);

        var types = deck
            .GroupBy(card => card.Type)
            .OrderBy(group => (int)group.Key)
            .Select(group => $"{CardTypeTag(group.Key)}×{group.Count()}");
        var rarities = deck
            .GroupBy(card => card.Rarity)
            .OrderBy(group => (int)group.Key)
            .Select(group => $"{RarityLabel(group.Key)}×{group.Count()}");

        (string Label, string[] Keys)[] keywords =
        [
            ("抽牌", ["抽", "Draw"]),
            ("消耗", ["消耗", "Exhaust"]),
            ("格挡", ["格挡", "Block"]),
            ("力量", ["力量", "Strength"]),
            ("易伤", ["易伤", "Vulnerable"]),
            ("中毒", ["中毒", "Poison"]),
            ("辉星", ["辉星", "Star"])
        ];
        var keywordCounts = keywords
            .Select(keyword => (keyword.Label,
                Count: descriptions.Count(desc => ContainsAny(desc, keyword.Keys))))
            .Where(pair => pair.Count > 0)
            .Select(pair => $"{pair.Label}×{pair.Count}");

        var sb = new StringBuilder();
        sb.AppendLine($"{who} 牌组({deck.Count}张): {string.Join(" ", types)}");
        sb.AppendLine($"平均费用{averageCost:F1} | 已升级{upgraded}张 | {string.Join(" ", rarities)}");
        var keywordText = string.Join(" ", keywordCounts);
        if (!string.IsNullOrWhiteSpace(keywordText))
            sb.Append($"关键词: {keywordText}");

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Nodes reachable from the current position, mirroring <c>NMapScreen.RecalculateTravelability</c>:
    /// the act's starting point (the Ancient room) before the first move, the boss after the last
    /// normal row, and the second boss after the first boss.
    /// </summary>
    public static IReadOnlyList<MapPoint> GetTravelableMapPoints(Player aiPlayer)
    {
        var runState = RunManager.Instance?.DebugOnlyGetState();
        var map = runState?.Map;
        if (runState == null || map == null)
            return Array.Empty<MapPoint>();

        // No node visited yet: the map screen only enables StartingMapPoint (the Ancient/start node of
        // this act). Returning map.startMapPoints (the row-1 nodes) here let the AI vote straight past
        // the act-start Ancient, skipping it entirely.
        if (runState.VisitedMapCoords.Count == 0)
            return SortPoints(new[] { map.StartingMapPoint });

        if (runState.CurrentMapPoint is not { } current)
            return Array.Empty<MapPoint>();

        if (map.SecondBossMapPoint is { } secondBoss && current.coord == map.BossMapPoint.coord)
            return new[] { secondBoss };

        if (current.coord.row == map.GetRowCount() - 1)
            return new[] { map.BossMapPoint };

        return SortPoints(MapTravel.GetTravelablePointsFrom(runState, current));
    }

    private static IReadOnlyList<MapPoint> SortPoints(IEnumerable<MapPoint> points) =>
        points
            .OrderBy(point => point.coord.row)
            .ThenBy(point => point.coord.col)
            .ToList();

    /// <summary>Coords visited in the current act (only available on the concrete RunState).</summary>
    private static IReadOnlyList<MapCoord> GetVisitedMapCoords()
    {
        return RunManager.Instance?.DebugOnlyGetState()?.VisitedMapCoords ?? Array.Empty<MapCoord>();
    }

    /// <summary>Map node label used by prompts, tools and the solo navigator.</summary>
    public static string DescribeMapPoint(MapPoint point) =>
        $"{MapPointTypeLabel(point.PointType)}(第{point.coord.row}行,第{point.coord.col}列)";

    public static string MapPointTypeLabel(MapPointType type) => type switch
    {
        MapPointType.Monster => "战斗",
        MapPointType.Elite => "精英",
        MapPointType.RestSite => "篝火",
        MapPointType.Shop => "商店",
        MapPointType.Treasure => "宝箱",
        MapPointType.Ancient => "远古",
        MapPointType.Boss => "Boss",
        _ => "未知"
    };

    private static string RarityLabel(CardRarity rarity) => rarity switch
    {
        CardRarity.None => "无",
        CardRarity.Basic => "基础",
        CardRarity.Common => "普通",
        CardRarity.Uncommon => "罕见",
        CardRarity.Rare => "稀有",
        CardRarity.Ancient => "远古",
        CardRarity.Event => "事件",
        CardRarity.Token => "衍生",
        CardRarity.Status => "状态",
        CardRarity.Curse => "诅咒",
        CardRarity.Quest => "任务",
        _ => rarity.ToString()
    };

    private static bool ContainsAny(string text, string[] keys) =>
        !string.IsNullOrEmpty(text) && keys.Any(key => text.Contains(key, StringComparison.OrdinalIgnoreCase));

    /// <summary>All cards the AI can currently see (piles + master deck), for card lookups.</summary>
    public static IEnumerable<CardModel> EnumerateVisibleCards(Player aiPlayer)
    {
        var combat = aiPlayer.PlayerCombatState;
        if (combat != null)
        {
            foreach (var card in combat.Hand.Cards) yield return card;
            foreach (var card in combat.DrawPile.Cards) yield return card;
            foreach (var card in combat.DiscardPile.Cards) yield return card;
            foreach (var card in combat.ExhaustPile.Cards) yield return card;
        }

        if (aiPlayer.Deck?.Cards is { } deck)
        {
            foreach (var card in deck) yield return card;
        }
    }

    private static string FormatPile(string label, IEnumerable<CardModel>? cards, PileType pileType,
        int maxEntries, bool fullDetail, bool unknownOrder)
    {
        var list = cards?.Where(c => c != null).ToList() ?? new List<CardModel>();
        if (list.Count == 0)
            return $"{label}: 空";

        var groups = list
            .GroupBy(c => (c.Id.Entry, c.IsUpgraded, c.Title))
            .Select(g => (Card: g.First(), Count: g.Count()))
            .OrderBy(g => (int)g.Card.Type)
            .ThenBy(g => PromptTextHelper.ResolveEnergyCost(g.Card))
            .ThenBy(g => g.Card.Title ?? g.Card.Id.Entry, StringComparer.Ordinal)
            .ToList();

        var sb = new StringBuilder();
        sb.Append($"{label}({list.Count}张{(unknownOrder ? "，顺序未知" : "")}): ");

        var shown = 0;
        foreach (var (card, count) in groups)
        {
            if (shown >= maxEntries)
                break;
            if (shown > 0)
                sb.Append(", ");
            sb.Append(FormatPileCard(card, count, fullDetail, pileType));
            shown++;
        }
        if (groups.Count > shown)
            sb.Append($", …另{groups.Count - shown}种未列出");

        return sb.ToString();
    }

    public static string CardTypeTag(CardType type) => type switch
    {
        CardType.Attack => "攻",
        CardType.Skill => "技",
        CardType.Power => "能",
        CardType.Status => "状",
        CardType.Curse => "咒",
        CardType.Quest => "任",
        _ => ""
    };

    /// <summary>Compact cost text such as <c>1费</c>, <c>2费1星</c>, <c>X费</c>. 费用简述。</summary>
    public static string FormatCostCompact(CardModel card)
    {
        var energyCost = card.EnergyCost;
        var costStr = energyCost is { CostsX: true }
            ? "X费"
            : $"{PromptTextHelper.ResolveEnergyCost(card)}费";
        if (card.HasStarCostX)
            return costStr + "X星";

        var starCost = Math.Max(0, card.GetStarCostWithModifiers());
        return starCost > 0 ? $"{costStr}{starCost}星" : costStr;
    }

    private static string FormatPileCard(CardModel card, int count, bool fullDetail, PileType pileType)
    {
        var name = card.Title ?? card.Id.Entry;
        var costStr = FormatCostCompact(card);
        var typeTag = CardTypeTag(card.Type);
        var countStr = count > 1 ? $"×{count}" : "";

        if (!fullDetail)
            return $"{name}{countStr}({costStr}{typeTag})";

        var desc = PromptTextHelper.CleanText(PromptTextHelper.GetCardDescriptionText(card, pileType));
        return string.IsNullOrWhiteSpace(desc)
            ? $"{name}{countStr}({costStr}{typeTag})"
            : $"{name}{countStr}({costStr}{typeTag}): {desc}";
    }

    /// <summary>Show which enemies are targeting which players, and whether the AI is in danger.</summary>
    private string BuildThreatAssessmentText(CombatState state, Player aiPlayer)
    {
        try
        {
            var aiCreature = aiPlayer.Creature;
            if (aiCreature == null) return "";

            // Find enemies attacking this turn
            var attackingEnemies = state.Enemies
                .Where(e => e.IsAlive && e.Monster?.NextMove?.Intents.Any(i => i is AttackIntent) == true)
                .ToList();

            if (attackingEnemies.Count == 0) return "";

            // Estimate total incoming damage to AI
            int totalIncoming = 0;
            foreach (var enemy in attackingEnemies)
            {
                try
                {
                    var atkIntents = enemy.Monster!.NextMove!.Intents.OfType<AttackIntent>();
                    foreach (var atk in atkIntents)
                    {
                        totalIncoming += atk.GetSingleDamage(
                            state.Players.Select(p => p.Creature),
                            enemy);
                    }
                }
                catch { /* skip if damage calc fails */ }
            }

            var block = aiCreature.Block;
            var hp = aiCreature.CurrentHp;
            var netDmg = Math.Max(0, totalIncoming - block);

            if (totalIncoming > 0)
            {
                var dangerLabel = netDmg >= hp ? "!!致死!!" :
                                  netDmg > hp / 2 ? "危险" :
                                  block >= totalIncoming ? "安全" : "";
                var threat = $"预计受到{totalIncoming}点伤害";
                if (block > 0) threat += $" 格挡{block}";
                threat += $" 净伤害{netDmg}/{hp}HP";
                if (!string.IsNullOrEmpty(dangerLabel)) threat += $" {dangerLabel}";
                return threat;
            }
        }
        catch { }

        return "";
    }

    public string BuildChatContext(IReadOnlyList<ChatMessage> recentMessages, int recentWindow = 4)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[聊天上下文]");
        if (recentMessages.Count == 0)
        {
            sb.AppendLine("(无)");
            return sb.ToString();
        }

        var older = recentMessages.Take(Math.Max(0, recentMessages.Count - recentWindow)).TakeLast(6).ToList();
        if (older.Count > 0)
        {
            sb.AppendLine("较早摘要:");
            sb.AppendLine(string.Join(" | ", older.Select(msg =>
                $"{(msg.IsAi ? "你" : msg.SenderName)}:{PromptTextHelper.Truncate(PromptTextHelper.CleanText(msg.Content), 24)}")));
            sb.AppendLine();
        }

        sb.AppendLine("最近对话:");
        foreach (var msg in recentMessages.TakeLast(recentWindow))
            sb.AppendLine($"{(msg.IsAi ? "你" : msg.SenderName)}: {PromptTextHelper.Truncate(PromptTextHelper.CleanText(msg.Content), 80)}");

        return sb.ToString();
    }

    // ---- Non-combat contexts ----

    public string BuildCardRewardContext(Player aiPlayer, List<CardModel> choices)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[卡牌奖励]");
        AppendPlayerBrief(sb, aiPlayer);

        sb.AppendLine();
        for (int i = 0; i < choices.Count; i++)
        {
            var card = choices[i];
            if (card == null) continue;
            var detail = FormatCardDetail(card);
            var preview = _query.FormatUpgradePreview(card, 90);
            sb.AppendLine(string.IsNullOrWhiteSpace(preview) ? $"{i}. {detail}" : $"{i}. {detail} | {preview}");
        }
        sb.AppendLine("-1=跳过");

        AppendDeckList(sb, aiPlayer);

        return sb.ToString();
    }

    public string BuildMapContext(Player aiPlayer)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[地图导航]");
        AppendPlayerBrief(sb, aiPlayer);

        var map = DescribeMap(aiPlayer);
        if (!string.IsNullOrWhiteSpace(map))
        {
            sb.AppendLine();
            sb.AppendLine(map);
        }

        return sb.ToString();
    }

    public string BuildRestSiteContext(Player aiPlayer, int currentHp, int maxHp, IReadOnlyList<RestSiteOption> options)
    {
        var sb = new StringBuilder();
        var hpPct = maxHp > 0 ? (currentHp * 100 / maxHp) : 0;
        var hpLabel = hpPct switch
        {
            <= 20 => "【危重】",
            <= 40 => "【危险】",
            <= 60 => "【偏低】",
            _ => "【健康】"
        };
        sb.AppendLine($"[休息站] {hpLabel}{GetCharacterName(aiPlayer)} HP:{currentHp}/{maxHp}({hpPct}%) Gold:{aiPlayer.Gold}");

        // Teammate HP status — helps decide MEND, HEAL, etc.
        try
        {
            var runState = aiPlayer.RunState;
            if (runState != null)
            {
                var teammates = runState.Players.Where(p => p != aiPlayer).ToList();
                if (teammates.Count > 0)
                {
                    var teammateInfos = teammates.Select(t =>
                    {
                        var alive = t.Creature?.IsAlive ?? false;
                        var tHp = t.Creature?.CurrentHp ?? 0;
                        var tMax = t.Creature?.MaxHp ?? 1;
                        var tPct = tMax > 0 ? (tHp * 100 / tMax) : 0;
                        var tLabel = !alive ? "【阵亡】" :
                                     tPct <= 40 ? "⚠" :
                                     tPct >= 100 ? "满血" : "";
                        return $"{GetCharacterName(t)} HP:{tHp}/{tMax}({tPct}%){tLabel}";
                    });
                    sb.AppendLine($"队友: {string.Join(" | ", teammateInfos)}");

                    // Warn if MEND has no valid target
                    var aliveInjured = teammates.Any(t => t.Creature?.IsAlive == true
                        && (t.Creature?.CurrentHp ?? 0) < (t.Creature?.MaxHp ?? 1));
                    if (!aliveInjured)
                        sb.AppendLine("⚠ 当前没有受伤的队友，MEND（治疗队友）将无效！");
                }
            }
        }
        catch { }

        // Show deck + act info
        var deck = aiPlayer.Deck.Cards;
        var upgradable = deck.Where(c => c.IsUpgradable).Take(3).Select(c => c.Title).ToList();
        var totalUpgradable = deck.Count(c => c.IsUpgradable);
        sb.Append($"牌组:{deck.Count}张 可升级:{totalUpgradable}张");
        if (upgradable.Count > 0)
        {
            var sample = string.Join(",", upgradable);
            if (totalUpgradable > 3) sample += $"...等{totalUpgradable}张";
            sb.Append($" ({sample})");
        }
        sb.AppendLine();

        // Add act/floor context for better decision making
        try
        {
            var runState = aiPlayer.RunState;
            if (runState != null)
                sb.AppendLine($"当前: 第{runState.CurrentActIndex + 1}幕 第{runState.TotalFloor}层");
        }
        catch { }

        // Relics that affect rest decisions
        var relics = aiPlayer.Relics;
        if (relics != null && relics.Any())
        {
            var keyRelics = relics.Where(r =>
            {
                var id = r.Id.Entry.ToLowerInvariant();
                return id.Contains("coffee") || id.Contains("dripper") || id.Contains("fusion")
                    || id.Contains("hammer") || id.Contains("shovel") || id.Contains("peace")
                    || id.Contains("girya") || id.Contains("pipe") || id.Contains("dream");
            }).ToList();
            if (keyRelics.Count > 0)
                sb.AppendLine($"[关键遗物] {string.Join(", ", keyRelics.Select(r => r.Id.Entry))} (可能影响休息站选择)");
        }

        sb.AppendLine();
        for (int i = 0; i < options.Count; i++)
        {
            var option = options[i];
            var title = SafeLocText(option.Title);
            var desc = SafeLocText(option.Description);
            // Truncate description to keep prompt compact
            desc = PromptTextHelper.Truncate(desc, 60);
            var enabledTag = option.IsEnabled ? "" : " [不可用]";
            sb.AppendLine($"{i}. {title}{enabledTag} — {desc}");
        }

        var smithOption = options.OfType<SmithRestSiteOption>().FirstOrDefault();
        if (smithOption?.IsEnabled == true)
        {
            var upgradableCards = aiPlayer.Deck.Cards
                .Where(c => c.IsUpgradable)
                .ToList();

            sb.AppendLine();
            sb.AppendLine($"如果选择 SMITH，本次需要从以下可升级卡牌中选择 1~{smithOption.SmithCount} 张，并返回对应索引：");
            for (int i = 0; i < upgradableCards.Count; i++)
            {
                var card = upgradableCards[i];
                var brief = GetCardOneLiner(card, 60);
                var preview = _query.FormatUpgradePreview(card, 90);
                var line = $"- {i}. {card.Title}({FormatCostCompact(card)}) 当前: {brief}";
                sb.AppendLine(string.IsNullOrWhiteSpace(preview) ? line : $"{line} | {preview}");
            }
        }

        return sb.ToString();
    }

    /// <summary>Resolve a LocString for prompts, preferring formatted text with concrete values.</summary>
    private static string SafeLocText(LocString? loc)
    {
        return PromptTextHelper.ResolveLocString(loc);
    }

    public string BuildShopContext(Player aiPlayer, List<ShopDecisionEntry> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[商店]");
        sb.Append(GetHpStatusLabel(aiPlayer));
        AppendPlayerBrief(sb, aiPlayer);

        sb.AppendLine();
        for (int i = 0; i < entries.Count; i++)
        {
            sb.AppendLine($"{i}. {entries[i].DisplayText}");
        }
        sb.AppendLine("（返回空 choices 数组表示离开商店）");

        var cardKnowledge = _query.FormatCardKnowledge(entries.Where(e => e.Card != null).Select(e => e.Card!), maxCount: 8);
        if (!string.IsNullOrWhiteSpace(cardKnowledge))
        {
            sb.AppendLine();
            sb.AppendLine("[候选卡牌资料]");
            sb.AppendLine(cardKnowledge);
        }

        var relicKnowledge = _query.FormatRelicKnowledge(
            entries.Where(e => e.Relic != null).Select(e => e.Relic!)
                .Concat(entries.SelectMany(e => e.ModifyingRelics)),
            maxCount: 8);
        if (!string.IsNullOrWhiteSpace(relicKnowledge))
        {
            sb.AppendLine();
            sb.AppendLine("[候选遗物资料]");
            sb.AppendLine(relicKnowledge);
        }

        var potionKnowledge = _query.FormatPotionKnowledge(entries.Where(e => e.Potion != null).Select(e => e.Potion!), maxCount: 4);
        if (!string.IsNullOrWhiteSpace(potionKnowledge))
        {
            sb.AppendLine();
            sb.AppendLine("[候选药水资料]");
            sb.AppendLine(potionKnowledge);
        }

        AppendDeckList(sb, aiPlayer);

        return sb.ToString();
    }

    /// <summary>Full (deduplicated) master deck, for decisions that depend on deck composition.</summary>
    private static void AppendDeckList(StringBuilder sb, Player aiPlayer)
    {
        if (!AiSettingsStore.Current.EnablePileDetail)
            return;

        var deck = aiPlayer.Deck?.Cards;
        if (deck == null || deck.Count == 0)
            return;

        sb.AppendLine();
        sb.AppendLine("[当前牌组]");
        sb.AppendLine(FormatPile("主牌组", deck, PileType.Deck, MaxToolPileEntries, fullDetail: false, unknownOrder: false));
    }

    public string BuildEventContext(Player aiPlayer, List<string> optionDescs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[事件决策]");
        sb.Append(GetHpStatusLabel(aiPlayer));
        AppendPlayerBrief(sb, aiPlayer);

        sb.AppendLine();
        sb.AppendLine("事件选项：");
        for (int i = 0; i < optionDescs.Count; i++)
            sb.AppendLine($"{i}. {PromptTextHelper.CleanText(optionDescs[i])}");

        var enrichment = _query.FormatMentionKnowledge(optionDescs, maxCount: 8);
        if (enrichment.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("[相关资料]");
            sb.Append(enrichment);
        }

        return sb.ToString();
    }

    public string BuildHextechRuneContext(Player aiPlayer, List<RuneOptionInfo> options, MonsterHexInfo? monsterHex)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[海克斯符文选择]");

        AppendPlayerBrief(sb, aiPlayer);

        var strategy = BuildRunStrategyLabel(aiPlayer);
        if (!string.IsNullOrWhiteSpace(strategy))
            sb.AppendLine($"构筑策略: {strategy}");

        var relics = aiPlayer.Relics;
        if (relics != null && relics.Any())
        {
            var keyRelics = relics
                .Where(r =>
                {
                    var id = r.Id.Entry.ToLowerInvariant();
                    return id.Contains("strength") || id.Contains("dexterity")
                        || id.Contains("energy") || id.Contains("draw")
                        || id.Contains("block") || id.Contains("damage")
                        || id.Contains("vulnerable") || id.Contains("weak")
                        || id.Contains("poison") || id.Contains("orb");
                })
                .Take(5)
                .Select(r => r.Id.Entry)
                .ToList();
            if (keyRelics.Count > 0)
                sb.AppendLine($"关键遗物: {string.Join(", ", keyRelics)}");
        }

        try
        {
            var runState = aiPlayer.RunState;
            if (runState != null)
                sb.AppendLine($"当前: 第{runState.CurrentActIndex + 1}幕 第{runState.TotalFloor}层");
        }
        catch { }

        sb.AppendLine();

        if (monsterHex != null && !monsterHex.IsRemoved)
        {
            sb.AppendLine($"[敌人Hex] {monsterHex.Title}");
            if (!string.IsNullOrWhiteSpace(monsterHex.Description))
                sb.AppendLine($"  效果: {monsterHex.Description}");
        }
        else
        {
            sb.AppendLine("[敌人Hex] 已移除或无");
        }
        sb.AppendLine();

        sb.AppendLine("符文选项（三选一）：");
        for (int i = 0; i < options.Count; i++)
        {
            var opt = options[i];
            var desc = PromptTextHelper.Truncate(opt.Description, 120);
            sb.AppendLine($"{i}. {opt.Name}");
            if (!string.IsNullOrWhiteSpace(desc))
                sb.AppendLine($"   {desc}");
        }

        var allNames = string.Join(" ", options.Select(o => o.Name));
        var enrichment = _query.EnrichContext(allNames);
        if (enrichment.Length > 0)
        {
            sb.AppendLine();
            sb.Append("[相关资料]");
            sb.Append(enrichment);
        }

        return sb.ToString();
    }

    /// <summary>Compact one-line player state for non-combat contexts.</summary>
    private void AppendPlayerBrief(StringBuilder sb, Player p)
    {
        var c = p.Creature;
        var deck = p.Deck?.Cards;
        var hpPct = c.MaxHp > 0 ? (c.CurrentHp * 100 / c.MaxHp) : 0;
        sb.Append($"{GetCharacterName(p)} | HP:{c.CurrentHp}/{c.MaxHp}({hpPct}%) | Gold:{p.Gold}");
        if (deck != null)
        {
            var basicCount = deck.Count(c => c.Rarity == CardRarity.Basic);
            sb.Append($" | 牌组:{deck.Count}张");
        }
        sb.AppendLine();
    }

    /// <summary>Returns a Chinese HP status label for the given player.</summary>
    public static string GetHpStatusLabel(Player p)
    {
        var c = p.Creature;
        if (c == null || c.MaxHp <= 0) return "";
        var pct = c.CurrentHp * 100 / c.MaxHp;
        return pct switch
        {
            <= 20 => $"【血量危重 {c.CurrentHp}/{c.MaxHp}】 ",
            <= 40 => $"【血量危险 {c.CurrentHp}/{c.MaxHp}】 ",
            <= 60 => $"【血量偏低 {c.CurrentHp}/{c.MaxHp}】 ",
            _ => ""
        };
    }

    public string BuildRunStrategicSummary(Player aiPlayer)
    {
        var runState = aiPlayer.RunState;
        var deck = aiPlayer.Deck?.Cards ?? new List<CardModel>();
        var hpLabel = GetHpStatusLabel(aiPlayer).Trim();
        var strategy = BuildRunStrategyLabel(aiPlayer);
        var stage = runState?.TotalFloor switch
        {
            null => "阶段未知",
            <= 10 => "前期",
            <= 30 => "中期",
            _ => "后期"
        };

        var sb = new StringBuilder();
        if (runState != null)
        {
            // Ascension changes enemy HP/damage and elite density, so the LLM should know it is playing one.
            var ascension = runState.AscensionLevel > 0 ? $" | 进阶{runState.AscensionLevel}" : "";
            sb.AppendLine($"第{runState.CurrentActIndex + 1}幕 第{runState.TotalFloor}层{ascension} | {stage} | {hpLabel}".Trim(' ', '|'));
        }

        if (!string.IsNullOrWhiteSpace(strategy))
            sb.AppendLine($"构筑判断: {strategy}");

        if (deck.Count > 0)
        {
            var avgCost = deck.Average(card => (double)PromptTextHelper.ResolveEnergyCost(card));
            sb.AppendLine($"资源概况: 牌组{deck.Count}张 | 金币{aiPlayer.Gold} | 平均费用{avgCost:F1}");
        }

        var keyCards = deck
            .GroupBy(card => card.Title ?? card.Id.Entry)
            .OrderByDescending(group => ScoreCardForStrategy(group.First()) + group.Count())
            .Take(4)
            .Select(group => group.Key)
            .ToList();
        if (keyCards.Count > 0)
            sb.AppendLine($"关键牌线索: {string.Join(" / ", keyCards)}");

        return sb.ToString().TrimEnd();
    }

    public string BuildRunStrategyLabel(Player aiPlayer)
    {
        var scores = AnalyzeDeckStrategy(aiPlayer);
        if (scores.Count == 0)
            return "方向尚未成型";

        var top = scores.OrderByDescending(kvp => kvp.Value).Take(2).Where(kvp => kvp.Value > 0).ToList();
        if (top.Count == 0)
            return "方向尚未成型";

        return string.Join(" + ", top.Select(kvp => kvp.Key));
    }

    private Dictionary<string, int> AnalyzeDeckStrategy(Player aiPlayer)
    {
        var scores = new Dictionary<string, int>
        {
            ["叠甲防御"] = 0,
            ["力量爆发"] = 0,
            ["消耗循环"] = 0,
            ["抽牌运转"] = 0,
            ["易伤压制"] = 0,
            ["中毒持续"] = 0
        };

        foreach (var card in aiPlayer.Deck?.Cards ?? Enumerable.Empty<CardModel>())
        {
            var text = $"{card.Title} {PromptTextHelper.ResolveCardDescription(card, PileType.Hand, card.Id.Entry)}";
            AddIfContains(text, scores, "叠甲防御", "格挡", "护甲", "壁垒", "防御");
            AddIfContains(text, scores, "力量爆发", "力量", "伤害", "攻击");
            AddIfContains(text, scores, "消耗循环", "消耗", "耗尽", "烧毁");
            AddIfContains(text, scores, "抽牌运转", "抽", "检索", "弃");
            AddIfContains(text, scores, "易伤压制", "易伤", "破甲");
            AddIfContains(text, scores, "中毒持续", "中毒", "毒");
        }

        return scores;
    }

    private static void AddIfContains(string text, Dictionary<string, int> scores, string key, params string[] keywords)
    {
        foreach (var keyword in keywords)
        {
            if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                scores[key]++;
        }
    }

    private int ScoreCardForStrategy(CardModel card)
    {
        var text = $"{card.Title} {PromptTextHelper.ResolveCardDescription(card, PileType.Hand, card.Id.Entry)}";
        var score = 0;
        if (text.Contains("格挡")) score += 2;
        if (text.Contains("力量")) score += 2;
        if (text.Contains("消耗")) score += 2;
        if (text.Contains("易伤")) score += 2;
        if (text.Contains("抽")) score += 1;
        return score;
    }

    public string GetCardOneLiner(CardModel card, int maxLen = 80)
    {
        var desc = PromptTextHelper.ResolveCardDescription(card, PileType.Hand, card.Id.Entry);
        desc = PromptTextHelper.CleanText(desc);
        var periodIdx = desc.IndexOf('.');
        var oneLiner = periodIdx > 0 ? desc[..(periodIdx + 1)] : desc;
        return PromptTextHelper.Truncate(oneLiner, maxLen);
    }

    /// <summary>获取玩家的显示名称，优先使用平台名称，回退到"玩家X"</summary>
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

        // 回退：使用 NetId 后4位生成名称
        return $"玩家{(int)(player.NetId & 0xFFFF)}";
    }
}
