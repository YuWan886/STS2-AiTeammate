using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Saves.Runs;
using AiTeammate.AiTeammateCode.Compat;

namespace AiTeammate.AiTeammateCode.Patches;

/// <summary>
/// Prevents CombatStateSynchronizer from blocking on AI players who have no real network peer, by
/// replacing <c>CheckSyncCompleted</c> with a version that skips AI netIds.
///
/// The replacement owns the whole method, which on 0.113+ also contains the host's one-shot
/// RNG / shared-relic broadcast — see <see cref="CombatSyncRngCompat"/>. Forgetting that broadcast
/// leaves every client without a RNG set, stuck in <c>WaitForSync</c>: black screen before the
/// first room, exactly the 2026-11-17 Dread reports.
/// </summary>
[HarmonyPatch(typeof(CombatStateSynchronizer), "CheckSyncCompleted")]
public static class CombatStateSynchronizerPatch
{
    [HarmonyPrefix]
    public static bool BeforeCheckSyncCompleted(CombatStateSynchronizer __instance)
    {
        var syncData = (Dictionary<ulong, SerializablePlayer>?)
            AccessTools.Field(typeof(CombatStateSynchronizer), "_syncData").GetValue(__instance);
        var runLobby = (RunLobby?)
            AccessTools.Field(typeof(CombatStateSynchronizer), "_runLobby").GetValue(__instance);
        var tcs = (TaskCompletionSource?)
            AccessTools.Field(typeof(CombatStateSynchronizer), "_syncCompletionSource").GetValue(__instance);
        var rngSet = AccessTools.Field(typeof(CombatStateSynchronizer), "_rngSet").GetValue(__instance);
        var netService = (INetGameService?)
            AccessTools.Field(typeof(CombatStateSynchronizer), "_netService").GetValue(__instance);

        if (tcs == null) return false;
        if (runLobby == null || netService == null)
            throw new InvalidOperationException("Tried to combat sync without a valid run lobby!");

        if (netService.Type == NetGameType.Singleplayer || __instance.IsDisabled)
        {
            tcs.SetResult();
            return false;
        }

        foreach (ulong id in Compat.GameCompat.ConnectedPlayerIds(runLobby))
        {
            // Skip AI players — they have no real network peer
            if (AiIdentity.IsAi(id))
                continue;
            if (syncData == null || !syncData.ContainsKey(id))
                return false; // still waiting for real players
        }

        // The AI itself is replicated over the mod channel. Do not finish the sync before the
        // host's authoritative snapshot is in, or the first combat checksum compares stale state.
        if (netService.Type == NetGameType.Client && AiStateSync.AwaitingCombatSnapshot
            && !AiStateSync.IsCombatSnapshotOverdue)
        {
            return false;
        }

        // 0.113+ only accepts "everybody is in" here *and* sends the host's RNG / shared relic grab
        // bag from here. Replacing the method therefore also owns that broadcast: without it no
        // client ever leaves WaitForSync.
        CombatSyncRngCompat.PublishIfDeferred(__instance, netService, rngSet);

        if (rngSet != null)
            tcs.SetResult();

        return false; // skip original method
    }
}
