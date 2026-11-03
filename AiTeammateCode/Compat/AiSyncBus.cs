using System.Collections.Generic;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace AiTeammate.AiTeammateCode.Compat;

/// <summary>
/// Registered mod-message plumbing for <see cref="AiSyncMessage"/>.
///
/// The host publishes; clients either receive a fire-and-forget payload (state snapshot) or complete
/// a pending <see cref="AwaitAsync"/> call (combat plans / out-of-combat choices). Messages received
/// before the corresponding await starts are cached so a fast host never starves a slow client.
/// </summary>
internal static class AiSyncBus
{
    public const byte KindCombatPlan = 1;
    public const byte KindChoice = 2;
    public const byte KindState = 3;

    private const int MaxCachedMessages = 128;

    private static INetGameService? _registered;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, string> Received = new();
    private static readonly Dictionary<string, TaskCompletionSource<string>> Waiters = new();

    /// <summary>Binds the handler to the current run's net service (idempotent per service).</summary>
    public static void EnsureRegistered() => EnsureRegistered(RunManager.Instance?.NetService);

    public static void EnsureRegistered(INetGameService? net)
    {
        if (ReferenceEquals(net, _registered))
            return;

        var previous = _registered;
        _registered = net;

        if (previous != null)
        {
            try { previous.UnregisterMessageHandler<AiSyncMessage>(HandleMessage); }
            catch (Exception ex) { MainFile.Logger.Warn($"[Sync] 注销联机处理器失败: {ex.Message}"); }
        }

        if (net == null)
            return;

        try
        {
            net.RegisterMessageHandler<AiSyncMessage>(HandleMessage);
            MainFile.Logger.Info($"[Sync] 已在 {net.GetType().Name} 上注册 AI 同步");
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Sync] 注册联机处理器失败: {ex.Message}");
        }
    }

    /// <summary>Clears buffered decisions when a new run starts.</summary>
    public static void ResetForNewRun()
    {
        lock (Gate)
        {
            Received.Clear();
            Waiters.Clear();
        }
    }

    /// <summary>True when the host is reachable and a sync message can actually leave this peer.</summary>
    public static bool CanPublish
    {
        get
        {
            var net = RunManager.Instance?.NetService;
            return net != null && net.IsConnected && net.Type == NetGameType.Host;
        }
    }

    /// <summary>Host: broadcast a decision payload. No-op (and false) outside a networked host.</summary>
    public static bool Publish(byte kind, string key, string json)
    {
        if (!CanPublish)
            return false;

        try
        {
            RunManager.Instance!.NetService!.SendMessage(new AiSyncMessage
            {
                kind = kind,
                key = key ?? string.Empty,
                json = json ?? string.Empty,
                hasPlayer = false
            });
            return true;
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Sync] 广播消息失败 ({key}): {ex.Message}");
            return false;
        }
    }

    /// <summary>Host: broadcast the authoritative AI player snapshot.</summary>
    public static bool PublishState(SerializablePlayer player, string reason)
    {
        if (!CanPublish || player == null)
            return false;

        try
        {
            RunManager.Instance!.NetService!.SendMessage(new AiSyncMessage
            {
                kind = KindState,
                key = reason ?? string.Empty,
                json = string.Empty,
                player = player,
                hasPlayer = true
            });
            return true;
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Sync] 广播 AI 状态失败 ({reason}): {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Client: wait for the host's payload for this exact context. Returns null on timeout — callers
    /// must then take a deterministic local fallback so the turn/room cannot deadlock.
    /// </summary>
    public static async Task<string?> AwaitAsync(byte kind, string key, TimeSpan timeout, CancellationToken ct = default)
    {
        var id = Id(kind, key);

        lock (Gate)
        {
            if (Received.Remove(id, out var cached))
                return cached;

            if (!Waiters.ContainsKey(id))
                Waiters[id] = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        TaskCompletionSource<string> waiter;
        lock (Gate)
            waiter = Waiters[id];

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, ct);
        await using var registration = linked.Token.Register(() => waiter.TrySetCanceled());

        try
        {
            return await waiter.Task;
        }
        catch (OperationCanceledException)
        {
            MainFile.Logger.Warn($"[Sync] 等待房主决策超时 ({key})");
            return null;
        }
        finally
        {
            lock (Gate)
                Waiters.Remove(id);
        }
    }

    private static string Id(byte kind, string key) => $"{kind}:{key}";

    private static void HandleMessage(AiSyncMessage message, ulong senderId)
    {
        // Only clients mirror; the host is the source of truth (and the host's own broadcasts are
        // never looped back to it).
        if (RunManager.Instance?.NetService?.Type != NetGameType.Client)
            return;

        if (message.kind == KindState)
        {
            var snapshot = message.player;
            MainThread.Post(() => AiStateSync.Apply(snapshot, message.key));
            return;
        }

        var id = Id(message.kind, message.key);
        TaskCompletionSource<string>? waiter = null;
        lock (Gate)
        {
            if (!Waiters.TryGetValue(id, out waiter))
            {
                if (Received.Count >= MaxCachedMessages)
                    Received.Clear();
                Received[id] = message.json ?? string.Empty;
                return;
            }
        }

        waiter.TrySetResult(message.json ?? string.Empty);
    }
}
