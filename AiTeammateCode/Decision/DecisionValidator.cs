using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;

namespace AiTeammate.AiTeammateCode.Decision;

public class DecisionValidator
{
    public List<DecisionAction> Validate(List<DecisionAction> actions, Player aiPlayer)
        => Validate(actions, aiPlayer, null);

    /// <summary>
    /// Validates one decision. When <paramref name="dropped"/> is supplied, every action this method
    /// refuses is described there (card name + reason), so the broker can show dsh what did not run.
    /// 校验决策；可选地把被丢弃的动作与原因写进 dropped。
    /// </summary>
    public List<DecisionAction> Validate(List<DecisionAction> actions, Player aiPlayer, List<string>? dropped)
    {
        var validActions = new List<DecisionAction>();
        var combat = aiPlayer.PlayerCombatState;
        var state = CombatManager.Instance.DebugOnlyGetState();

        if (combat == null || state == null)
            return validActions;

        foreach (var action in actions)
        {
            if (action.Type == "end_turn")
            {
                validActions.Add(action);
                continue;
            }

            if (action.Type == "suggest_card")
            {
                validActions.Add(action);
                continue;
            }

            if (action.Type == "play_card" && !string.IsNullOrWhiteSpace(action.CardName))
            {
                var card = FindCardInHand(combat, action.CardName);
                if (card == null)
                {
                    dropped?.Add($"{action.CardName}: 不在手牌");
                    continue;
                }

                if (card.Keywords.Contains(CardKeyword.Unplayable))
                {
                    dropped?.Add($"{action.CardName}: 不可打出");
                    continue;
                }

                if (!combat.HasEnoughResourcesFor(card, out var reason))
                {
                    dropped?.Add($"{action.CardName}: 资源不足（{reason}）");
                    continue;
                }

                var targetCheck = ResolveTargetForCard(state, aiPlayer, card, action);
                if (targetCheck.IsError)
                {
                    dropped?.Add($"{action.CardName}: {targetCheck.Error}");
                    continue;
                }

                validActions.Add(action);
            }

            if (action.Type == "use_potion" && !string.IsNullOrWhiteSpace(action.PotionName))
            {
                var potion = aiPlayer.Potions?.FirstOrDefault(p =>
                {
                    var title = PromptTextHelper.ResolveLocString(p.Title, p.Id.Entry);
                    return title.Contains(action.PotionName, System.StringComparison.OrdinalIgnoreCase)
                        || p.Id.Entry.Contains(action.PotionName, System.StringComparison.OrdinalIgnoreCase);
                });
                if (potion != null)
                {
                    // PotionUsage.Automatic (Fairy in a Bottle) has no manual Use button in vanilla;
                    // the AI must not drink it as a heal. 自动触发的药水不能手动喝。
                    if (!PromptTextHelper.CanBeUsedManually(potion))
                    {
                        dropped?.Add($"药水{action.PotionName}: 只能自动触发，不能手动使用");
                        continue;
                    }
                    var potionCheck = ResolveTargetForPotion(state, aiPlayer, potion, action);
                    if (potionCheck.IsError)
                    {
                        dropped?.Add($"药水{action.PotionName}: {potionCheck.Error}");
                        continue;
                    }
                    validActions.Add(action);
                }
                else
                {
                    dropped?.Add($"药水{action.PotionName}: 未找到");
                }
            }

            if (action.Type is not ("play_card" or "use_potion" or "suggest_card" or "end_turn"))
                dropped?.Add($"{action.Type}: 未知动作");
        }

        if (validActions.Count == 0)
            validActions.Add(new DecisionAction { Type = "end_turn" });

        return validActions;
    }

    public List<DecisionAction> ValidateDeferred(List<DecisionAction> deferred, Player aiPlayer,
        List<string>? dropped = null)
    {
        // Validate deferred actions just like normal ones (they'll be checked again at play time)
        var combat = aiPlayer.PlayerCombatState;
        var state = CombatManager.Instance.DebugOnlyGetState();
        if (combat == null || state == null) return new();

        var valid = new List<DecisionAction>();
        foreach (var action in deferred)
        {
            if (string.IsNullOrWhiteSpace(action.CardName)) continue;

            var card = FindCardInHand(combat, action.CardName);
            if (card == null)
            {
                dropped?.Add($"{action.CardName}: 不在手牌（延迟动作）");
                continue;
            }
            if (card.Keywords.Contains(CardKeyword.Unplayable))
            {
                dropped?.Add($"{action.CardName}: 不可打出（延迟动作）");
                continue;
            }

            var targetCheck = ResolveTargetForCard(state, aiPlayer, card, action);
            if (targetCheck.IsError)
            {
                dropped?.Add($"{action.CardName}: {targetCheck.Error}（延迟动作）");
                continue;
            }

            valid.Add(action);
        }
        return valid;
    }

    private CardModel? FindCardInHand(PlayerCombatState combat, string cardName)
    {
        return combat.Hand.Cards.FirstOrDefault(c =>
        {
            var title = c.Title ?? c.Id.Entry;
            return title.Contains(cardName, System.StringComparison.OrdinalIgnoreCase)
                || c.Id.Entry.Contains(cardName, System.StringComparison.OrdinalIgnoreCase);
        });
    }

    public List<DecisionAction> ApplyFallback(Player aiPlayer)
    {
        var combat = aiPlayer.PlayerCombatState;
        if (combat == null) return new() { new() { Type = "end_turn" } };

        var state = CombatManager.Instance.DebugOnlyGetState();
        var actions = new List<DecisionAction>();
        var directiveManager = AiTeammateManager.Instance.DirectiveManager;
        var directivePotionAction = directiveManager.TryBuildPotionAssistFallback(aiPlayer);
        if (directivePotionAction != null)
            actions.Add(directivePotionAction);

        int energy = combat.Energy;
        int stars = combat.Stars;

        // All playable cards (not unplayable, affordable by both energy and stars)
        var playable = combat.Hand.Cards
            .Where(c => !c.Keywords.Contains(CardKeyword.Unplayable)
                     && (PromptTextHelper.ResolveEnergyCost(c)) <= energy
                     && Math.Max(0, c.GetStarCostWithModifiers()) <= stars)
            .ToList();

        if (playable.Count == 0)
        {
            actions.Add(new DecisionAction { Type = "end_turn" });
            return actions;
        }

        // Check if any enemy is attacking (needs block)
        bool enemyAttacking = state?.Enemies.Any(e =>
            e.IsAlive && e.Monster?.NextMove?.Intents.Any(i =>
                i.IntentType == IntentType.Attack) == true) ?? false;

        var preferredTargetName = directiveManager.GetPreferredFocusTarget();

        // Priority: Attacks first (especially against low-HP enemies)
        var attacks = playable
            .Where(c => c.Type == CardType.Attack)
            .OrderByDescending(c => PromptTextHelper.ResolveEnergyCost(c))
            .ToList();

        foreach (var card in attacks)
        {
            var cost = PromptTextHelper.ResolveEnergyCost(card);
            var starCost = Math.Max(0, card.GetStarCostWithModifiers());
            if (cost > energy || starCost > stars) continue;

            // Pick target: prefer the directive's focus target, else the lowest-HP alive enemy.
            // The chosen creature is written as "#index" AND locked on the action, so the executor
            // cannot re-resolve the name onto a same-named twin (the old code stored only the name,
            // which is exactly how "planned the low-HP one, hit the high-HP one" happened).
            Creature? targetEnemy = null;
            if (state != null)
            {
                if (!string.IsNullOrWhiteSpace(preferredTargetName))
                {
                    var preferred = CombatTargetResolver.Resolve(state, aiPlayer, preferredTargetName,
                        TargetScope.Enemy);
                    if (!preferred.IsError)
                        targetEnemy = preferred.Creature;
                }
                targetEnemy ??= state.Enemies
                    .Where(e => e.IsAlive)
                    .OrderBy(e => e.CurrentHp)
                    .FirstOrDefault();
            }

            var targetIndex = CombatTargetResolver.IndexOf(state, targetEnemy);
            actions.Add(new DecisionAction
            {
                Type = "play_card",
                CardName = card.Title ?? card.Id.Entry,
                Target = targetIndex >= 0 ? $"#{targetIndex}" : "",
                ResolvedTarget = targetEnemy
            });
            energy -= cost;
            stars -= starCost;
        }

        // If enemies are attacking, play skills (defense)
        if (enemyAttacking && energy > 0)
        {
            var skills = playable
                .Where(c => c.Type == CardType.Skill)
                .OrderByDescending(c => PromptTextHelper.ResolveEnergyCost(c))
                .ToList();

            foreach (var card in skills)
            {
                var cost = PromptTextHelper.ResolveEnergyCost(card);
                var starCost = Math.Max(0, card.GetStarCostWithModifiers());
                if (cost > energy || starCost > stars) continue;
                if (card.Keywords.Contains(CardKeyword.Unplayable)) continue;

                actions.Add(new DecisionAction
                {
                    Type = "play_card",
                    CardName = card.Title ?? card.Id.Entry,
                    Target = ""
                });
                energy -= cost;
                stars -= starCost;
            }
        }

        // Play any remaining powers
        if (energy > 0)
        {
            var powers = playable
                .Where(c => c.Type == CardType.Power)
                .OrderByDescending(c => PromptTextHelper.ResolveEnergyCost(c))
                .ToList();

            foreach (var card in powers)
            {
                var cost = PromptTextHelper.ResolveEnergyCost(card);
                var starCost = Math.Max(0, card.GetStarCostWithModifiers());
                if (cost > energy || starCost > stars) continue;

                actions.Add(new DecisionAction
                {
                    Type = "play_card",
                    CardName = card.Title ?? card.Id.Entry,
                    Target = ""
                });
                energy -= cost;
                stars -= starCost;
            }
        }

        actions.Add(new DecisionAction { Type = "end_turn" });
        return actions;
    }

    /// <summary>
    /// Resolves the target once, at validation time, and stores the creature on the action so the
    /// executor plays exactly that instance (an enemy dying mid-pass shifts the enemy list, so
    /// re-resolving the string later could land on a different creature).
    /// 校验阶段锁定目标实例；解析失败直接丢弃动作（绝不交给 AutoPlay 去随机挑一个）。
    /// </summary>
    private static TargetResolution ResolveTargetForCard(CombatState state, Player aiPlayer, CardModel card,
        DecisionAction action)
    {
        if (card.TargetType == TargetType.AnyEnemy)
        {
            var resolved = CombatTargetResolver.Resolve(state, aiPlayer, action.Target, TargetScope.Enemy);
            if (!resolved.IsError)
                action.ResolvedTarget = resolved.Creature;
            return resolved;
        }

        if (card.TargetType == TargetType.AnyAlly)
        {
            var resolved = CombatTargetResolver.Resolve(state, aiPlayer, action.Target, TargetScope.Ally);
            if (resolved.IsError)
                return resolved;
            if (resolved.Creature == null)
                return TargetResolution.Fail("支援牌必须写队友名或 self");
            action.ResolvedTarget = resolved.Creature;
            return resolved;
        }

        // Self / AllEnemies / ... are played with target: null (the hand UI does the same).
        action.ResolvedTarget = null;
        return TargetResolution.None;
    }

    private static TargetResolution ResolveTargetForPotion(CombatState state, Player aiPlayer, PotionModel potion,
        DecisionAction action)
    {
        if (potion.TargetType == TargetType.AnyEnemy)
        {
            var resolved = CombatTargetResolver.Resolve(state, aiPlayer, action.Target, TargetScope.Enemy);
            if (!resolved.IsError)
                action.ResolvedTarget = resolved.Creature;
            return resolved;
        }

        if (potion.TargetType == TargetType.AnyAlly)
        {
            var resolved = CombatTargetResolver.Resolve(state, aiPlayer, action.Target, TargetScope.Ally);
            if (resolved.IsError)
                return resolved;
            if (resolved.Creature == null)
                return TargetResolution.Fail("这瓶药水要指定队友");
            action.ResolvedTarget = resolved.Creature;
            return resolved;
        }

        if (potion.TargetType == TargetType.AnyPlayer)
        {
            // 0.111 potions are throwable at any player (self or a teammate), so the directive
            // "给队友用药水" arrives here as action.Target. Empty target (or a self alias) means
            // "drink it myself" — UsePotionAction already defaults a null target to the owner.
            // Singleplayer has no teammate to throw at: never fail the potion over that.
            if (string.IsNullOrWhiteSpace(action.Target) || state.Players.Count <= 1)
            {
                action.ResolvedTarget = null;
                return TargetResolution.None;
            }

            var resolved = CombatTargetResolver.Resolve(state, aiPlayer, action.Target, TargetScope.Any);
            if (resolved.IsError)
                return resolved;
            if (resolved.Creature == null || resolved.Creature == aiPlayer.Creature)
            {
                action.ResolvedTarget = null;
                return TargetResolution.None;
            }
            if (!resolved.Creature.IsPlayer)
                return TargetResolution.Fail("这瓶药水只能对玩家使用，不能对敌人使用");
            action.ResolvedTarget = resolved.Creature;
            return resolved;
        }

        // Self / AllEnemies / ... are used with target: null (the executor defaults them to the AI).
        action.ResolvedTarget = null;
        return TargetResolution.None;
    }

}
