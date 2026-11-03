using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using AiTeammate.AiTeammateCode.Compat;

namespace AiTeammate.AiTeammateCode.Patches;

/// <summary>
/// <c>PlayerChoiceSynchronizer</c> stamps every choice message with the transport sender id, not the
/// player the choice belongs to. When the mod syncs an AI card-selection result the other peers
/// therefore apply it to the sending *human*. The AI's choices are made locally on every peer (the
/// mod forces <c>ShouldSelectLocalCard</c> for AI players), so they must never be broadcast.
/// </summary>
[HarmonyPatch(typeof(PlayerChoiceSynchronizer), nameof(PlayerChoiceSynchronizer.SyncLocalChoice))]
public static class AiChoiceBroadcastSkipPatch
{
    [HarmonyPrefix]
    public static bool Prefix(Player player)
    {
        return !AiIdentity.IsAi(player);
    }
}
