using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;

namespace AiTeammate.AiTeammateCode.Compat;

/// <summary>
/// The AI teammate has no network peer of its own, but every peer simulates it locally. That means
/// every state-changing AI decision must be made EXACTLY ONCE and then applied identically on every
/// peer — otherwise the deterministic simulation diverges and the game kicks the human players.
///
/// The host owns that role ("authority"). Singleplayer/replay own it trivially.
/// Clients ("followers") never call the LLM for the AI; they wait for the host's decision and apply it.
/// </summary>
internal static class AiMultiplayer
{
    /// <summary>True on the peer that owns the AI's decisions (host / singleplayer / replay).</summary>
    public static bool IsAuthority
    {
        get
        {
            var type = RunManager.Instance?.NetService?.Type;
            return type is null or NetGameType.None or NetGameType.Singleplayer or NetGameType.Replay
                or NetGameType.Host;
        }
    }

    /// <summary>True on a client that mirrors a host-driven AI teammate.</summary>
    public static bool IsFollower => !IsAuthority;

    /// <summary>True when the current run actually talks to other peers.</summary>
    public static bool IsNetworked
    {
        get
        {
            var net = RunManager.Instance?.NetService;
            return net != null && net.IsConnected
                && net.Type is NetGameType.Host or NetGameType.Client;
        }
    }

    /// <summary>
    /// True when more than one human shares this run. With a single human every peer sees the same
    /// machine (singleplayer or host-alone), so the AI may keep its local LLM decisions.
    /// </summary>
    public static bool IsMultiHuman
    {
        get
        {
            if (!IsNetworked)
                return false;

            var state = RunManager.Instance?.DebugOnlyGetState();
            if (state == null)
            {
                // Host without a state yet: assume networked (a client is connected).
                return RunManager.Instance?.NetService?.Type == NetGameType.Client;
            }

            var humans = 0;
            foreach (var player in state.Players)
            {
                if (!AiIdentity.IsAi(player))
                    humans++;
            }
            return humans > 1;
        }
    }

    /// <summary>
    /// True when a decision must be synchronized with the other peers. Two humans always do; a host
    /// that is alone with the AI does not need to (there is nobody to diverge from).
    /// </summary>
    public static bool RequiresDecisionSync => IsNetworked && (IsFollower || IsMultiHuman);

    public static Player? AiPlayer => AiTeammateManager.Instance.AiPlayer;
}
