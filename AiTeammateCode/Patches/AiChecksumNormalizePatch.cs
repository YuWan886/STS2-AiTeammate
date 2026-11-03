using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using AiTeammate.AiTeammateCode.Compat;

namespace AiTeammate.AiTeammateCode.Patches;

/// <summary>
/// The AI player's relic grab bag is host-authoritative: only the host rolls the AI's rewards,
/// events and shops, and the resulting bag is replicated over the mod channel
/// (<see cref="AiStateSync"/>). A client's local copy can still be one iteration behind at the exact
/// moment a checksum is generated, which used to trip vanilla's deterministic checksum and kick the
/// human with <c>StateDivergence</c>.
///
/// Dropping the AI bag from the checksum snapshot on every peer (identically, so the comparison is
/// still meaningful for everything else) keeps the check for real divergences while making the AI's
/// own private RNG state non-fatal.
/// </summary>
[HarmonyPatch(typeof(NetFullCombatState), nameof(NetFullCombatState.FromRun))]
public static class AiChecksumNormalizePatch
{
    [HarmonyPostfix]
    public static void Postfix(NetFullCombatState __result)
    {
        try
        {
            if (__result?.Players == null)
                return;

            foreach (var playerState in __result.Players)
            {
                if (!AiIdentity.IsAi(playerState.playerId))
                    continue;

                var bag = playerState.relicGrabBag;
                if (bag?.RelicIdLists == null || bag.RelicIdLists.Count == 0)
                    continue;

                bag.RelicIdLists.Clear();
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Sync] 归一化 AI 遗物池校验失败: {ex.Message}");
        }
    }
}
