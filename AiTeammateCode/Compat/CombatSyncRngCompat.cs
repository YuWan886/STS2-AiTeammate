using System;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Game;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace AiTeammate.AiTeammateCode.Compat;

/// <summary>
/// 0.113 moved the host's RNG / shared-relic broadcast out of <c>StartSync</c> and into
/// <c>CheckSyncCompleted</c>: the host still snapshots its RNG in StartSync, but only sends
/// <c>SyncRngMessage</c> once every player's data is in (one-shot, guarded by <c>_hasSentRngSet</c>).
///
/// <c>Patches.CombatStateSynchronizerPatch</c> replaces that method — the vanilla loop also waits for
/// the AI teammate, which has no network peer and never reports in — so the mod has to re-issue the
/// deferred broadcast itself. Without it every client keeps <c>_rngSet == null</c>, never leaves
/// <c>WaitForSync</c>, and sits on a black screen before the first room (Neow) ever renders. That is
/// exactly the 2026-11-17 Dread report: the host walked into EVENT.NEOW while no client logged a
/// single event, and the client trace stopped on "Waiting to receive all sync messages".
/// </summary>
internal static class CombatSyncRngCompat
{
    /// <summary>0.113+ only: false until the host's one-shot RNG broadcast has gone out.</summary>
    private static readonly FieldInfo? HasSentRngSet =
        AccessTools.Field(typeof(CombatStateSynchronizer), "_hasSentRngSet");

    private static readonly FieldInfo? SharedRelicGrabBag =
        AccessTools.Field(typeof(CombatStateSynchronizer), "_sharedRelicGrabBag");

    private static bool _loggedLegacySend;

    /// <summary>
    /// Host only: publish the deferred <c>SyncRngMessage</c> once the completion check has decided
    /// that every real player reported in. No-op on ≤0.111.x, where <c>StartSync</c> still sends it.
    /// </summary>
    public static void PublishIfDeferred(CombatStateSynchronizer sync, INetGameService netService, object? rngSet)
    {
        if (netService.Type != NetGameType.Host || rngSet == null)
            return;

        if (HasSentRngSet == null)
        {
            // Older snapshots have no such field because StartSync itself sends the message.
            if (!_loggedLegacySend)
            {
                _loggedLegacySend = true;
                MainFile.Logger.Info("[Sync] 本版本由 StartSync 自行广播 RNG/共享遗物池，无需补发");
            }
            return;
        }

        try
        {
            if (HasSentRngSet.GetValue(sync) is true)
                return;

            if (rngSet is not SerializableRunRngSet rng)
                return;

            if (SharedRelicGrabBag?.GetValue(sync) is not SerializableRelicGrabBag grabBag)
            {
                MainFile.Logger.Warn("[Sync] 找不到共享遗物池快照，跳过 RNG 同步补发");
                return;
            }

            HasSentRngSet.SetValue(sync, true);
            netService.SendMessage(new SyncRngMessage { rng = rng, sharedRelicGrabBag = grabBag });
            MainFile.Logger.Info("[Sync] 已补发 RNG/共享遗物池同步（0.113+ 的广播点在被替换的 CheckSyncCompleted 里）");
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Sync] 补发 RNG 同步失败: {ex.Message}");
        }
    }
}
