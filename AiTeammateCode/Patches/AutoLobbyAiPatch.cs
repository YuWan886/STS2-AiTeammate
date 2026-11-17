using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using AiTeammate.AiTeammateCode.Compat;

namespace AiTeammate.AiTeammateCode.Patches;

/// <summary>
/// Adds the AI teammate automatically to every multiplayer lobby that has no vanilla "Add AI"
/// control: daily, custom, and (beta 0.113.1+) Dread and Blitz.
///
/// <c>StartRunLobby.IsAboutToBeginGame</c> refuses to begin while a multiplayer lobby holds exactly
/// one player, and those screens offer no way to add a second one, so a lone host could never start
/// a run there. All of them build their lobby through this single call, so one postfix covers every
/// non-standard mode; a standard lobby keeps the character-select screen's own AI panel instead.
/// 每日 / 自定 / Dread / Blitz 大厅没有「添加 AI」按钮，建房时自动加入并准备 AI。
/// </summary>
[HarmonyPatch(typeof(StartRunLobby), nameof(StartRunLobby.AddLocalHostPlayer))]
internal static class AutoLobbyAiPatch
{
    [HarmonyPostfix]
    public static void Postfix(StartRunLobby __instance)
    {
        try
        {
            if (!GameCompat.IsAutoAiLobby(__instance))
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
