using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Models;

namespace AiTeammate.AiTeammateCode.Decision;

/// <summary>
/// Local fallback for the treasure-chest relic vote: used when the model (or dsh) answer is missing,
/// unusable or late, so the chest still resolves without a human. Rarity is the only signal that
/// works for every relic including modded ones, so it carries the score; a relic whose id names the
/// AI's own character gets a small nudge. Equal scores are broken randomly so runs differ.
/// 宝箱遗物本地兜底：模型没答案/超时/解析失败时按稀有度选，角色专属加一点分；同分随机。
/// </summary>
internal static class TreasureRelicHeuristic
{
    public static int Pick(Player aiPlayer, IReadOnlyList<RelicModel> relics)
    {
        if (relics == null || relics.Count == 0)
            return -1;

        var best = 0;
        var bestScore = int.MinValue;
        for (var i = 0; i < relics.Count; i++)
        {
            var score = Score(aiPlayer, relics[i]);
            if (score > bestScore)
            {
                bestScore = score;
                best = i;
            }
            else if (score == bestScore && System.Random.Shared.Next(2) == 0)
            {
                best = i;
            }
        }

        MainFile.Logger.Info(
            $"[TreasureRelic] 启发式选择 index={best} name={SafeId(relics[best])} score={bestScore}");
        return best;
    }

    private static int Score(Player aiPlayer, RelicModel? relic)
    {
        if (relic == null)
            return 0;

        var score = RarityScore(relic);

        try
        {
            var charId = (aiPlayer?.Character?.Id.Entry ?? "").ToLowerInvariant();
            if (charId.Length > 0)
            {
                var id = (relic.Id.Entry ?? "").ToLowerInvariant();
                if (id.Contains(charId))
                    score += 3;
            }
        }
        catch
        {
            // Character lookup is cosmetic scoring only.
        }

        return score;
    }

    private static int RarityScore(RelicModel relic)
    {
        try
        {
            return relic.Rarity switch
            {
                RelicRarity.Ancient => 50,
                RelicRarity.Rare => 40,
                RelicRarity.Shop => 35,
                RelicRarity.Event => 30,
                RelicRarity.Uncommon => 25,
                RelicRarity.Common => 20,
                RelicRarity.Starter => 10,
                _ => 15
            };
        }
        catch
        {
            return 0;
        }
    }

    private static string SafeId(RelicModel? relic)
    {
        try { return relic?.Id.Entry ?? "?"; }
        catch { return "?"; }
    }
}
