using System.Text.Json;
using MegaCrit.Sts2.Core.Entities.Players;
using AiTeammate.AiTeammateCode.Decision;

namespace AiTeammate.AiTeammateCode.Compat;

/// <summary>
/// Wire format for one combat decision pass. The host runs the LLM (or its own fallback) and
/// publishes the validated plan; every client executes exactly the same plan, because the AI's
/// cards are played with <c>CardCmd.AutoPlay</c> which mutates each peer's simulation locally and is
/// never sent through the action queue.
/// </summary>
internal static class AiCombatPlanCodec
{
    private sealed class PlanDto
    {
        public bool isFallback { get; set; }
        public string thinking { get; set; } = "";
        public string chat { get; set; } = "";
        public List<DecisionAction> actions { get; set; } = new();
        public List<DecisionAction> deferred { get; set; } = new();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        IncludeFields = false,
        WriteIndented = false
    };

    public static string Key(Player aiPlayer, int decisionPass)
    {
        var floor = aiPlayer.RunState?.TotalFloor ?? 0;
        var turn = aiPlayer.PlayerCombatState?.TurnNumber ?? 0;
        return $"{floor}:{turn}:{decisionPass}";
    }

    public static void Publish(Player aiPlayer, int decisionPass, DecisionResult result)
    {
        try
        {
            var dto = new PlanDto
            {
                isFallback = result.IsFallback,
                thinking = result.Thinking ?? "",
                chat = result.ChatMessage ?? "",
                actions = result.Actions ?? new List<DecisionAction>(),
                deferred = result.DeferredActions ?? new List<DecisionAction>()
            };
            var json = JsonSerializer.Serialize(dto, JsonOptions);
            if (AiSyncBus.Publish(AiSyncBus.KindCombatPlan, Key(aiPlayer, decisionPass), json))
                MainFile.Logger.Info($"[Sync] 已广播战斗计划 ({Key(aiPlayer, decisionPass)}, {dto.actions.Count} 张)");
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Sync] 广播战斗计划失败: {ex.Message}");
        }
    }

    public static DecisionResult? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            var dto = JsonSerializer.Deserialize<PlanDto>(json, JsonOptions);
            if (dto == null)
                return null;

            return new DecisionResult
            {
                IsFallback = dto.isFallback,
                Thinking = dto.thinking ?? "",
                ChatMessage = dto.chat ?? "",
                Actions = dto.actions ?? new List<DecisionAction>(),
                DeferredActions = dto.deferred ?? new List<DecisionAction>()
            };
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Sync] 解析战斗计划失败: {ex.Message}");
            return null;
        }
    }
}
