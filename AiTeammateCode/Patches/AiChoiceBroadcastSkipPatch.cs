using System;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using AiTeammate.AiTeammateCode.Compat;

namespace AiTeammate.AiTeammateCode.Patches;

/// <summary>
/// <c>PlayerChoiceSynchronizer</c> stamps every choice message with the transport sender id, not the
/// player the choice belongs to. When the mod syncs an AI card-selection result the other peers
/// therefore apply it to the sending *human*. The AI's choices are made locally on every peer (the
/// mod forces <c>ShouldSelectLocalCard</c> for AI players), so they must never be broadcast.
/// <para>
/// The local <c>PlayerChoiceReceived</c> event still has to fire, though: <c>CombatReplayWriter</c>
/// subscribes to it, and a replay recorded without the AI's choices waits forever for a remote
/// choice when it is played back. So raise the event ourselves and skip only <c>SendMessage</c>.
/// </para>
/// </summary>
[HarmonyPatch(typeof(PlayerChoiceSynchronizer), nameof(PlayerChoiceSynchronizer.SyncLocalChoice))]
public static class AiChoiceBroadcastSkipPatch
{
    private static readonly FieldInfo? ChoiceReceivedField =
        AccessTools.Field(typeof(PlayerChoiceSynchronizer), "PlayerChoiceReceived");

    [HarmonyPrefix]
    public static bool Prefix(PlayerChoiceSynchronizer __instance, Player player, uint choiceId,
        PlayerChoiceResult result)
    {
        if (!AiIdentity.IsAi(player))
            return true;

        RaiseLocalChoiceReceived(__instance, player, choiceId, result);
        return false;
    }

    private static void RaiseLocalChoiceReceived(PlayerChoiceSynchronizer synchronizer, Player player,
        uint choiceId, PlayerChoiceResult result)
    {
        try
        {
            if (ChoiceReceivedField?.GetValue(synchronizer)
                is Action<Player, uint, NetPlayerChoiceResult> handler)
            {
                handler(player, choiceId, result.ToNetData());
            }
        }
        catch (Exception ex)
        {
            // A replay writer failing must never break an actual choice.
            MainFile.Logger.Warn($"[AI] 本地 choice 事件派发失败: {ex.Message}");
        }
    }
}
