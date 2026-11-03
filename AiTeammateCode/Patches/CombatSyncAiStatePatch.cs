using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer;
using AiTeammate.AiTeammateCode.Compat;

namespace AiTeammate.AiTeammateCode.Patches;

/// <summary>
/// The game syncs every real player's full state (<c>SyncPlayerDataMessage</c>) right before a
/// combat and applies it in <c>WaitForSync</c>. The AI teammate has no peer and is skipped by
/// <see cref="CombatStateSynchronizerPatch"/>, so its local copy is never reconciled — which is how
/// the reported <c>relicGrabBag.Common</c> mismatch survived into the first combat.
///
/// After the host starts a sync, it pushes the AI's authoritative snapshot over the mod channel;
/// clients adopt it (out of combat) so the very next checksum is computed from identical state.
/// </summary>
[HarmonyPatch(typeof(CombatStateSynchronizer), nameof(CombatStateSynchronizer.StartSync))]
public static class CombatSyncAiStatePatch
{
    [HarmonyPostfix]
    public static void AfterStartSync()
    {
        try
        {
            var manager = AiTeammateManager.Instance;
            if (!manager.IsInitialized || manager.AiPlayer == null)
                return;

            if (AiMultiplayer.IsFollower)
            {
                // Hold the pre-combat sync until the host's authoritative AI snapshot lands.
                AiStateSync.BeginCombatSyncWait();
                return;
            }

            if (!AiMultiplayer.IsNetworked)
                return;

            AiSyncBus.EnsureRegistered();
            AiStateSync.BroadcastFromAuthority("combat-start");
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Sync] 战斗开始同步 AI 状态失败: {ex.Message}");
        }
    }
}
