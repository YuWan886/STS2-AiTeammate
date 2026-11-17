using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;
using AiTeammate.AiTeammateCode.Compat;

namespace AiTeammate.AiTeammateCode.Patches;

/// <summary>
/// Registers the mod's net-message handlers before <c>RunManager.Launch</c> releases the message
/// buffer.
///
/// Everything a peer receives between "run begins" and the launch completing is buffered by
/// <c>NetMessageBus</c> and dispatched exactly once, inside <c>Launch</c> →
/// <c>SetBufferMessages(false)</c>. The AI handlers used to be installed only from
/// <c>AiTeammateManager.OnAiPlayerJoined</c>, i.e. after the run state exists, so on a client the
/// host's <c>AiNicknameMessage</c> and first <c>AiSyncMessage</c> (the authoritative AI snapshot)
/// were released into a bus with no handler and dropped:
/// "Received message of type ...AiNicknameMessage, but no message handlers are registered for that
/// type!" (2026-11-17 Dread report, client L400/L421). Standard lobbies register early on the
/// character-select screen; Dread/Blitz lobbies have no such hook.
/// </summary>
[HarmonyPatch(typeof(RunManager), nameof(RunManager.Launch))]
internal static class RunLaunchAiSyncPatch
{
    [HarmonyPrefix]
    public static void BeforeLaunch(RunManager __instance)
    {
        try
        {
            var net = __instance.NetService;
            if (net == null)
                return;
            if (net.Type is not (NetGameType.Host or NetGameType.Client))
                return;

            AiNicknameSync.EnsureRegistered(net);
            AiSyncBus.EnsureRegistered(net);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Sync] 开局前注册联机消息处理器失败: {ex.Message}");
        }
    }
}
