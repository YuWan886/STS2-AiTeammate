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
/// Hangs the AI character card on every lobby screen that has no character picker for the AI:
/// daily, custom, and (beta 0.113.1+) Dread and Blitz.
///
/// Those screens are pushed through the shared <see cref="NSubmenuStack.Push(NSubmenu)"/>, so this
/// single postfix covers all of them without depending on their private initialization methods
/// (which differ between game versions). The hosted daily lobby only appears after the time-server
/// fetch, so the card polls its lobby accessor instead of reading it once.
///
/// Dread/Blitz types do not exist in the pre-0.113.1 snapshots, so they are matched by type name and
/// read through their public `Lobby` property.
/// 每日/自定/Dread/Blitz 大厅没有 AI 队友角色选择器，这里在界面推入时挂上 AI 卡片。
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
                default:
                    // Dread / Blitz lobbies (0.113.1+) are separate screen types that also give the
                    // human a picker but the AI teammate none. They cannot be named in a case label
                    // without breaking the older variants, so they are recognized by type name.
                    if (IsNewModeLobby(screen))
                        LobbyAiCharacterPanel.Attach(screen, () => SafeLobbyProperty(screen));
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

    /// <summary>Lobby screens that only exist from 0.113.1 on. 0.113.1 才有的 Dread/Blitz 大厅。</summary>
    private static bool IsNewModeLobby(NSubmenu screen)
    {
        return screen.GetType().Name is "NDreadScreen" or "NBlitzScreen";
    }

    /// <summary>Both new lobby screens expose the lobby as a public <c>Lobby</c> property.</summary>
    private static StartRunLobby? SafeLobbyProperty(NSubmenu screen)
    {
        try
        {
            return screen.GetType().GetProperty("Lobby")?.GetValue(screen) as StartRunLobby;
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
