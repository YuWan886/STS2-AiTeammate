using HarmonyLib;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using AiTeammate.AiTeammateCode.Compat;

namespace AiTeammate.AiTeammateCode.Patches;

/// <summary>
/// The action queue stores "who caused this action" in the sender id, not in the action itself:
/// <c>EnqueueAction(action, netService.NetId)</c> on the host, and the sender id for a client
/// request. For a real player those are the same. For the AI teammate the host enqueues actions
/// whose <see cref="GameAction.OwnerId"/> is the AI's reserved NetId — clients then rebuild the
/// action with the *host human* as its owner (treasure relic pick, map vote, potion use, act vote).
///
/// Rewriting the owner to the action's own OwnerId when it is an AI NetId makes every peer rebuild
/// the action for the AI player, which is what the host already did.
/// </summary>
[HarmonyPatch(typeof(ActionQueueSynchronizer), "EnqueueAction")]
public static class AiActionOwnerPatch
{
    [HarmonyPrefix]
    public static void Prefix(GameAction action, ref ulong actionOwnerId)
    {
        try
        {
            if (action == null)
                return;
            if (AiIdentity.IsAi(action.OwnerId) && actionOwnerId != action.OwnerId)
            {
                MainFile.Logger.Info($"[Sync] 动作归属修正: {action.GetType().Name} {actionOwnerId} -> {action.OwnerId}");
                actionOwnerId = action.OwnerId;
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Sync] 动作归属修正失败: {ex.Message}");
        }
    }
}
