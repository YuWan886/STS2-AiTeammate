using System.Collections;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Runs;

namespace AiTeammate.AiTeammateCode.Compat;

/// <summary>
/// Small shims for the game API differences between 0.107.1 (stable) and 0.111.0 (beta).
/// Everything that needs a per-version branch lives here so the rest of the mod is
/// version-agnostic.
/// </summary>
internal static class GameCompat
{
    /// <summary>0.111 made StartRunLobby.MaxPlayers a private field.</summary>
    public static int MaxPlayers(StartRunLobby lobby)
    {
#if STS2_API_0_111_OR_LATER
        return AccessTools.Field(typeof(StartRunLobby), "_maxPlayers")?.GetValue(lobby) is int max ? max : 4;
#else
        return lobby.MaxPlayers;
#endif
    }

    /// <summary>0.111 replaced RunLobby.ConnectedPlayerIds with the Players/PlayerIds pair.</summary>
    public static IEnumerable<ulong> ConnectedPlayerIds(RunLobby lobby)
    {
#if STS2_API_0_111_OR_LATER
        return lobby.PlayerIds;
#else
        return lobby.ConnectedPlayerIds;
#endif
    }

    /// <summary>0.111 added the chooser parameter to SignalPlayerChoiceBegun.</summary>
    public static Task SignalPlayerChoiceBegun(PlayerChoiceContext context, Player chooser, PlayerChoiceOptions options)
    {
#if STS2_API_0_111_OR_LATER
        return context.SignalPlayerChoiceBegun(chooser, options);
#else
        return context.SignalPlayerChoiceBegun(options);
#endif
    }

    /// <summary>0.111 made the act index part of the vote action so stale votes can be ignored.</summary>
    public static GameAction CreateVoteToMoveToNextAct(Player player)
    {
#if STS2_API_0_111_OR_LATER
        return new VoteToMoveToNextActAction(player, player.RunState?.CurrentActIndex ?? 0);
#else
        return new VoteToMoveToNextActAction(player);
#endif
    }

    /// <summary>
    /// Marks the AI player as connected/ready in a resumed-run lobby. In 0.107 the lobby tracks
    /// connection and readiness in two sets; 0.111 folds both into the player list entry.
    /// </summary>
    public static void MarkLoadLobbyPlayerConnected(LoadRunLobby lobby, ulong netId)
    {
#if STS2_API_0_111_OR_LATER
        // LoadRunLobbyPlayer is a struct, so add a new entry or update the existing one in place.
        var index = lobby.Players.FindIndex(p => p.id == netId);
        MegaCrit.Sts2.Core.Entities.Multiplayer.LoadRunLobbyPlayer player;
        if (index < 0)
        {
            player = new MegaCrit.Sts2.Core.Entities.Multiplayer.LoadRunLobbyPlayer
            {
                id = netId,
                isModded = true,
                isReady = true
            };
            lobby.Players.Add(player);
        }
        else
        {
            player = lobby.Players[index];
            player.isReady = true;
            lobby.Players[index] = player;
        }

        // The game always hands the listener the same entry that lives in Players.
        lobby.LobbyListener.PlayerConnected(player);
#else
        if (lobby.ConnectedPlayerIds.Add(netId))
            lobby.LobbyListener.PlayerConnected(netId);

        var readyPlayers = (HashSet<ulong>?)AccessTools.Field(typeof(LoadRunLobby), "_readyPlayers")?.GetValue(lobby);
        if (readyPlayers != null && readyPlayers.Add(netId))
            lobby.LobbyListener.PlayerReadyChanged(netId);
#endif
    }

    /// <summary>
    /// Completes the resting AI player's rest-site slot in <see cref="RestSiteSynchronizer"/>.
    ///
    /// 0.111 made <c>RestSiteRoom.Exit</c> await <c>RestSiteSynchronizer.AfterAllRestSitesCompleted()</c>,
    /// which awaits every <c>PlayerRestSite.completionTaskSource</c>. A player's slot is normally
    /// completed by its own OptionIndexChosen / RestSiteSkipped message, but an AI teammate has no
    /// net peer, so nothing ever arrives for it: the room exit never resumes and the next map travel
    /// parks on the fade-out (a black screen). 0.107 has no completion source — this is a no-op there.
    /// </summary>
    public static void CompleteAiRestSiteSlot(RestSiteSynchronizer sync, Player player)
    {
        try
        {
            var syncType = typeof(RestSiteSynchronizer);
            if (AccessTools.Field(syncType, "_restSites")?.GetValue(sync) is not IList restSites)
                return;
            if (AccessTools.Field(syncType, "_playerCollection")?.GetValue(sync) is not IPlayerCollection collection)
                return;

            var slot = collection.GetPlayerSlotIndex(player);
            if (slot < 0 || slot >= restSites.Count)
                return;

            CompleteRestSiteSlot(restSites[slot]);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[REST] CompleteAiRestSiteSlot failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Clears a single <c>PlayerRestSite</c>'s remaining options and completes its task source.
    /// Takes <see cref="object"/> because <c>PlayerRestSite</c> is a private nested type.
    /// </summary>
    public static void CompleteRestSiteSlot(object? playerRestSite)
    {
        if (playerRestSite == null)
            return;

        var type = playerRestSite.GetType();

        // Vanilla clears whatever is left when a player's rest site ends; the AI never picks twice.
        if (AccessTools.Field(type, "options")?.GetValue(playerRestSite) is IList options)
            options.Clear();

        if (AccessTools.Field(type, "completionTaskSource")?.GetValue(playerRestSite) is TaskCompletionSource completion)
            completion.TrySetResult();
    }
}
