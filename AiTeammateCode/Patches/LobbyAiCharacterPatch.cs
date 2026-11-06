using System;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.Screens.CustomRun;
using MegaCrit.Sts2.Core.Nodes.Screens.DailyRun;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using AiTeammate.AiTeammateCode.Ui;

namespace AiTeammate.AiTeammateCode.Patches;

/// <summary>
/// Hangs the AI character card on the daily / custom lobby screens.
///
/// Those screens are pushed through the shared <see cref="NSubmenuStack.Push(NSubmenu)"/>, so this
/// single postfix covers both without depending on their private initialization methods (which
/// differ between game versions). The hosted daily lobby only appears after the time-server fetch,
/// so the card polls its lobby accessor instead of reading it once.
/// 每日/自定义大厅没有 AI 队友角色选择器，这里在界面推入时挂上 AI 卡片。
/// </summary>
[HarmonyPatch(typeof(NSubmenuStack), nameof(NSubmenuStack.Push))]
internal static class LobbyAiCharacterPanelPatch
{
    // Both snapshots keep the daily lobby in this private field (same reflection the dsh menu
    // snapshot already relies on).
    private static readonly FieldInfo? DailyLobbyField = AccessTools.Field(typeof(NDailyRunScreen), "_lobby");

    [HarmonyPostfix]
    public static void Postfix(NSubmenu screen)
    {
        try
        {
            switch (screen)
            {
                case NDailyRunScreen daily:
                    LobbyAiCharacterPanel.Attach(daily, () => SafeDailyLobby(daily));
                    break;
                case NCustomRunScreen custom:
                    LobbyAiCharacterPanel.Attach(custom, () => SafeCustomLobby(custom));
                    break;
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Lobby] failed to hook the AI character card: {ex.Message}");
        }
    }

    private static StartRunLobby? SafeDailyLobby(NDailyRunScreen screen)
    {
        try
        {
            return DailyLobbyField?.GetValue(screen) as StartRunLobby;
        }
        catch
        {
            return null;
        }
    }

    private static StartRunLobby? SafeCustomLobby(NCustomRunScreen screen)
    {
        try
        {
            return screen.Lobby;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Back / Esc closes the AI character picker before it can pop the whole lobby screen.
/// </summary>
[HarmonyPatch(typeof(NSubmenuStack), nameof(NSubmenuStack.Pop))]
internal static class LobbyAiCharacterPickerBackPatch
{
    [HarmonyPrefix]
    public static bool Prefix(NSubmenuStack __instance)
    {
        try
        {
            return !LobbyAiCharacterPanel.TryClosePicker(__instance.Peek());
        }
        catch
        {
            return true;
        }
    }
}
