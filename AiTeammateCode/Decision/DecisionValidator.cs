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
                    validActions.Add(action);
                else
                    dropped?.Add($"药水{action.PotionName}: 未找到");
            }

            if (action.Type is not ("play_card" or "use_potion" or "suggest_card" or "end_turn"))
                dropped?.Add($"{action.Type}: 未知动作");
        }

        if (validActions.Count == 0)
            validActions.Add(new DecisionAction { Type = "end_turn" });

        return validActions;
    }

    public List<DecisionAction> ValidateDeferred(List<DecisionAction> deferred, Player aiPlayer)
    {
        // Validate deferred actions just like normal ones (they'll be checked again at play time)
        var combat = aiPlayer.PlayerCombatState;
        if (combat == null) return new();

        var valid = new List<DecisionAction>();
        foreach (var action in deferred)
        {
            if (string.IsNullOrWhiteSpace(action.CardName)) continue;

            var card = FindCardInHand(combat, action.CardName);
            if (card == null) continue;
            if (card.Keywords.Contains(CardKeyword.Unplayable)) continue;

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

            // Pick target: prefer lowest-HP alive enemy
            string? target = null;
            if (state != null)
            {
                var aliveEnemies = state.Enemies.Where(e => e.IsAlive).ToList();
                var targetEnemy = !string.IsNullOrWhiteSpace(preferredTargetName)
                    ? aliveEnemies.FirstOrDefault(e => EnemyMatches(e, preferredTargetName))
                    : null;
                targetEnemy ??= aliveEnemies
                    .OrderBy(e => e.CurrentHp)
                    .FirstOrDefault();
                if (targetEnemy != null)
                    target = targetEnemy.Name ?? targetEnemy.Monster?.Id.Entry;
            }

            actions.Add(new DecisionAction
            {
                Type = "play_card",
                CardName = card.Title ?? card.Id.Entry,
                Target = target ?? ""
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

    private static bool EnemyMatches(Creature enemy, string targetName)
    {
        var name = enemy.Name ?? "";
        var id = enemy.Monster?.Id.Entry ?? "";
        return name.Contains(targetName, StringComparison.OrdinalIgnoreCase)
            || id.Contains(targetName, StringComparison.OrdinalIgnoreCase)
            || targetName.Contains(name, StringComparison.OrdinalIgnoreCase)
            || targetName.Contains(id, StringComparison.OrdinalIgnoreCase);
    }
}
