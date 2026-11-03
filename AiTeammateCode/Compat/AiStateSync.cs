using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace AiTeammate.AiTeammateCode.Compat;

/// <summary>
/// Host-authoritative replication of the AI player's full state (deck, relics, potions, gold, RNG,
/// unlocks, relic grab bag, extra fields).
///
/// The AI player exists on every peer but has no network peer of its own, so the game's
/// <c>CombatStateSynchronizer</c> has nothing to sync for it and the mod skips it in
/// <c>CheckSyncCompleted</c>. Any locally-different iteration (LLM choice, reward roll, event page)
/// therefore drifts forever and eventually trips the checksum. This class repairs that: the host
/// broadcasts its snapshot at the safe boundaries and clients adopt it verbatim.
/// </summary>
internal static class AiStateSync
{
    private static SerializablePlayer? _pending;

    /// <summary>
    /// Client-side gate: <c>CombatStateSynchronizer.CheckSyncCompleted</c> must not finish the
    /// pre-combat sync before the host's AI snapshot has been applied, otherwise the first checksum
    /// of the combat is computed from a stale local AI copy.
    /// </summary>
    public static bool AwaitingCombatSnapshot { get; private set; }

    private static DateTime _combatSnapshotDeadline;

    public static void BeginCombatSyncWait()
    {
        if (!AiMultiplayer.IsFollower)
            return;
        if (AiTeammateManager.Instance.AiPlayer == null)
            return;

        AwaitingCombatSnapshot = true;
        _combatSnapshotDeadline = DateTime.UtcNow.AddSeconds(2.5);

        // Failsafe: the sync must never hang a human because a mod message went missing.
        _ = Task.Delay(TimeSpan.FromSeconds(2.5)).ContinueWith(_ =>
            MainThread.Post(() =>
            {
                if (!AwaitingCombatSnapshot)
                    return;
                MainFile.Logger.Warn("[Sync] 等待房主 AI 状态超时，继续战斗同步");
                AwaitingCombatSnapshot = false;
                RetriggerSyncCheck();
            }));
    }

    public static bool IsCombatSnapshotOverdue =>
        AwaitingCombatSnapshot && DateTime.UtcNow >= _combatSnapshotDeadline;

    public static void EndCombatSyncWait() => AwaitingCombatSnapshot = false;

    /// <summary>Re-runs the vanilla sync completion check after a late AI snapshot arrived.</summary>
    public static void RetriggerSyncCheck()
    {
        try
        {
            var sync = RunManager.Instance?.CombatStateSynchronizer;
            if (sync == null)
                return;
            AccessTools.Method(typeof(CombatStateSynchronizer), "CheckSyncCompleted")?.Invoke(sync, null);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Sync] 重新检查战斗同步失败: {ex.Message}");
        }
    }

    /// <summary>Host: push the current AI player state to every client.</summary>
    public static void BroadcastFromAuthority(string reason, bool onlyIfMultiplayer = true)
    {
        if (!AiMultiplayer.IsAuthority)
            return;
        if (onlyIfMultiplayer && !AiMultiplayer.IsNetworked)
            return;

        var player = AiTeammateManager.Instance.AiPlayer;
        if (player == null)
            return;

        try
        {
            AiSyncBus.EnsureRegistered();
            if (AiSyncBus.PublishState(player.ToSerializable(), reason))
                MainFile.Logger.Info($"[Sync] 已广播 AI 状态 ({reason})");
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Sync] 生成 AI 状态失败 ({reason}): {ex.Message}");
        }
    }

    /// <summary>Client: adopt the host's snapshot (main thread only).</summary>
    public static void Apply(SerializablePlayer snapshot, string reason)
    {
        if (snapshot == null)
            return;

        if (!AiIdentity.IsAi(snapshot.NetId))
        {
            MainFile.Logger.Warn($"[Sync] 忽略非 AI 玩家的状态快照 NetId={snapshot.NetId}");
            return;
        }

        // Mutating inventories mid-combat can fight the action queue; queue the snapshot and let the
        // next safe boundary (combat end / room entry) adopt it.
        if (CombatManager.Instance.IsInProgress && !CombatManager.Instance.IsOverOrEnding)
        {
            _pending = snapshot;
            MainFile.Logger.Info($"[Sync] 战斗进行中，暂存 AI 状态快照 ({reason})");
            return;
        }

        ApplyNow(snapshot, reason);
    }

    /// <summary>Applies a previously buffered snapshot when combat is no longer running.</summary>
    public static void ApplyPendingIfSafe(string reason)
    {
        var snapshot = _pending;
        if (snapshot == null)
            return;
        if (CombatManager.Instance.IsInProgress && !CombatManager.Instance.IsOverOrEnding)
            return;

        _pending = null;
        ApplyNow(snapshot, $"{reason} (pending)");
    }

    public static void ClearPending() => _pending = null;

    private static void ApplyNow(SerializablePlayer snapshot, string reason)
    {
        var state = RunManager.Instance?.DebugOnlyGetState();
        var aiPlayer = state?.GetPlayer(snapshot.NetId);
        if (aiPlayer == null)
        {
            _pending = snapshot;
            MainFile.Logger.Warn($"[Sync] 当前 RunState 中找不到 AI 玩家 NetId={snapshot.NetId}，暂存快照");
            return;
        }

        if (ReferenceEquals(AiTeammateManager.Instance.AiPlayer, null))
            AiTeammateManager.Instance.DetectAndBindAiPlayer();

        try
        {
            aiPlayer.SyncWithSerializedPlayer(snapshot);
            MainFile.Logger.Info($"[Sync] 已采用房主的 AI 状态 ({reason})");
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Sync] 应用 AI 状态失败 ({reason}): {ex.Message}");
        }

        if (AiMultiplayer.IsFollower && AwaitingCombatSnapshot)
        {
            EndCombatSyncWait();
            RetriggerSyncCheck();
        }
    }
}
