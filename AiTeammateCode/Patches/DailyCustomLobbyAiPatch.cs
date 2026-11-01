using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Runs;

namespace AiTeammate.AiTeammateCode.Patches;

/// <summary>
/// Daily and custom multiplayer lobbies have no "Add AI" button (only the standard character-select
/// screen does), so a host who is alone can never satisfy <c>IsAboutToBeginGame</c> — it refuses to
/// begin while a multiplayer lobby has exactly one player. Add the AI teammate automatically (and
/// ready it) whenever such a lobby is created as host.
/// 每日/自定大厅没有「添加 AI」按钮，房主单人永远开不了局；建房时自动加入并准备 AI。
/// </summary>
[HarmonyPatch(typeof(StartRunLobby), nameof(StartRunLobby.AddLocalHostPlayer))]
internal static class DailyCustomLobbyAiPatch
{
    [HarmonyPostfix]
    public static void Postfix(StartRunLobby __instance)
    {
        try
        {
            if (__instance.GameMode is not (GameMode.Daily or GameMode.Custom))
                return;
            if (__instance.NetService?.Type != NetGameType.Host)
                return;

            var manager = AiTeammateManager.Instance;
            if (manager == null || !manager.IsInitialized)
                return;
            if (manager.IsAiInLobby(__instance))
                return;

            var ai = manager.AddAiToLobby(__instance);
            if (ai == null)
                return;

            manager.SetAiReady(__instance, ready: true);
            MainFile.Logger.Info(
                $"[Agent] AI teammate auto-added to the {__instance.GameMode} host lobby (slot {ai.Value.slotId})");
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Agent] failed to auto-add the AI to the {__instance.GameMode} lobby: {ex.Message}");
        }
    }
}
