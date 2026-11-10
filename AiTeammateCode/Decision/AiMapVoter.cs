using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;
using AiTeammate.AiTeammateCode.Compat;

namespace AiTeammate.AiTeammateCode.Decision;

/// <summary>
/// Multiplayer map voting: the AI decides its own coordinate instead of copying a human vote.
/// One decision per map location; a human vote (when one exists) is only the fallback so the host
/// never waits forever on a failed or slow LLM call.
/// </summary>
internal static class AiMapVoter
{
    /// <summary>Bounded by the "out-of-combat decision limit" setting (default 30s).</summary>
    private static int DecisionTimeoutMs => DecisionService.OutOfCombatTimeoutMs;

    private static readonly object Gate = new();
    private static int _busy;
    private static MapCoord? _fallback;

    /// <summary>
    /// Starts an independent vote if the AI has not voted for this map yet. Call on the main thread.
    /// </summary>
    /// <param name="fallback">Coordinate to use when the decision fails; usually the human's vote.</param>
    public static void Request(
        Player aiPlayer,
        MapSelectionSynchronizer synchronizer,
        MapLocation source,
        MapCoord? fallback)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            // A decision is already running: remember a newly known human vote as the fallback.
            lock (Gate)
            {
                if (fallback.HasValue)
                    _fallback = fallback;
            }
            return;
        }

        lock (Gate)
            _fallback = fallback;

        try
        {
            // The AI slot may have been removed/re-added, and a save load rebuilds every Player
            // instance: GetVote / GetPlayerSlotIndex use reference equality, so the cached reference
            // would index -1. Resolve the live instance by NetId first.
            var runState = RunManager.Instance?.DebugOnlyGetState();
            var liveAi = runState?.GetPlayer(aiPlayer.NetId);
            if (runState == null || liveAi == null)
            {
                ResetState();
                return;
            }

            aiPlayer = liveAi;

            // Already voted for this map generation (includes votes cast before a reconnect).
            var existing = synchronizer.GetVote(aiPlayer);
            if (existing.HasValue && existing.Value.mapGenerationCount == synchronizer.MapGenerationCount)
            {
                ResetState();
                return;
            }

            var points = ContextBuilder.GetTravelableMapPoints(aiPlayer);
            _ = VoteAsync(aiPlayer, synchronizer.MapGenerationCount, source, points);
        }
        catch (Exception ex)
        {
            ResetState();
            MainFile.Logger.Warn($"[MapVote] AI map vote request failed: {ex.Message}");
        }
    }

    private static async Task VoteAsync(
        Player aiPlayer,
        int generation,
        MapLocation source,
        IReadOnlyList<MapPoint> points)
    {
        MapCoord? coord = null;
        var reason = "";

        try
        {
            var manager = AiTeammateManager.Instance;
            if (points.Count > 0 && manager.IsInitialized && manager.DecisionService != null)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(DecisionTimeoutMs));
                var descriptions = points.Select(ContextBuilder.DescribeMapPoint).ToList();
                var (choice, why) = await manager.DecisionService.DecideMapNavigationAsync(aiPlayer, descriptions, cts.Token);
                reason = why;
                if (choice >= 0 && choice < points.Count)
                    coord = points[choice].coord;
            }
        }
        catch (OperationCanceledException)
        {
            // Fall back to the human's coordinate below.
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[MapVote] AI map decision failed: {ex.Message}");
        }
        finally
        {
            MapCoord? fallback;
            lock (Gate)
            {
                fallback = _fallback;
                _fallback = null;
            }

            coord ??= fallback;
            try
            {
                // Run state and the network action should be touched from the main thread.
                MainThread.Run(() => CastVote(aiPlayer, source, generation, coord, reason));
            }
            catch (Exception ex)
            {
                MainFile.Logger.Warn($"[MapVote] AI vote dispatch failed: {ex.Message}");
            }

            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private static void ResetState()
    {
        lock (Gate)
            _fallback = null;
        Interlocked.Exchange(ref _busy, 0);
    }

    private static void CastVote(
        Player aiPlayer,
        MapLocation source,
        int generation,
        MapCoord? coord,
        string reason)
    {
        if (!coord.HasValue)
            return;

        try
        {
            var runState = RunManager.Instance?.DebugOnlyGetState();
            var synchronizer = RunManager.Instance?.MapSelectionSynchronizer;
            if (runState == null || synchronizer == null)
                return;

            // The map moved on (or regenerated) while the LLM was thinking: discard a stale vote.
            if (synchronizer.MapGenerationCount != generation || !runState.MapLocation.Equals(source))
                return;

            // Same staleness guard as Request: the reference captured when the decision started may
            // already belong to a previous run if the player saved and loaded meanwhile.
            var liveAi = runState.GetPlayer(aiPlayer.NetId);
            if (liveAi == null)
                return;
            aiPlayer = liveAi;

            var existing = synchronizer.GetVote(aiPlayer);
            if (existing.HasValue && existing.Value.mapGenerationCount == generation)
                return;

            // Last-line guard: only vote a node the map screen would actually enable. A stale or
            // hallucinated coordinate otherwise gets accepted by the synchronizer and drags the whole
            // party past the act-start Ancient room.
            var travelable = ContextBuilder.GetTravelableMapPoints(aiPlayer);
            if (!travelable.Any(point => point.coord.Equals(coord.Value)))
            {
                MainFile.Logger.Warn(
                    $"[MapVote] AI vote ({coord.Value.col},{coord.Value.row}) is not travelable " +
                    $"(allowed: {string.Join(",", travelable.Select(p => $"({p.coord.col},{p.coord.row})"))}); vote dropped.");
                return;
            }

            synchronizer.PlayerVotedForMapCoord(aiPlayer, source, new MapVote
            {
                mapGenerationCount = generation,
                coord = coord.Value
            });

            var coordText = $"({coord.Value.col},{coord.Value.row})";
            MainFile.Logger.Info($"[MapVote] AI voted {coordText} reason={reason}");

            var manager = AiTeammateManager.Instance;
            if (manager.AiLog != null)
                manager.AiLog.LogEvent("地图投票", $"{coordText} | 独立决策 | {reason}");
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[MapVote] AI vote failed: {ex.Message}");
        }
    }
}
