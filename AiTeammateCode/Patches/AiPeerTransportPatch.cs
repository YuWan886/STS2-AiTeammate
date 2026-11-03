using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer.Transport.Steam;
using AiTeammate.AiTeammateCode.Compat;

namespace AiTeammate.AiTeammateCode.Patches;

/// <summary>
/// The AI teammate lives in the host's lobby/run player list with a reserved NetId but has no
/// transport connection. Vanilla code occasionally sends a *targeted* message to every player id it
/// finds in that list (e.g. <c>StartRunLobby</c> announcing a newly joined human with
/// <c>PlayerJoinedMessage</c>). Steam's host throws
/// <c>InvalidOperationException: Could not find connection for peer …</c> for the AI's id and the
/// surrounding catch disconnects the human who was trying to join.
///
/// Dropping only messages addressed to reserved AI NetIds is safe: the AI never has a connection to
/// receive anything, and every AI-specific channel is a mod broadcast instead.
/// </summary>
[HarmonyPatch(typeof(SteamHost), nameof(SteamHost.SendMessageToClient))]
public static class SteamHostAiPeerSkipPatch
{
    [HarmonyPrefix]
    public static bool Prefix(ulong peerId)
    {
        return !AiIdentity.IsAi(peerId);
    }
}
