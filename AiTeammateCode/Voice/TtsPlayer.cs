using AiTeammate.AiTeammateCode.Compat;
using AiTeammate.AiTeammateCode.Settings;
using Godot;
using MegaCrit.Sts2.Core.Nodes;

namespace AiTeammate.AiTeammateCode.Voice;

/// <summary>
/// Plays synthesized MP3 bytes through a Godot <see cref="AudioStreamPlayer"/> pool that lives on
/// the mod's own <see cref="TtsAudioBus"/> (see there for why the voice must not ride the game's
/// <c>SFX</c> bus). The bus carries the volume setting; the players only add the fallback offset.
///
/// Playback is line-aware: the segments of one chat line queue up behind each other, while a new
/// line may cut the previous one (interrupt). Main thread only; the service marshals every call.
/// 语音播放：AudioStreamPlayer 池 + 独立语音总线，同一条台词的多个分段顺序播放。
/// </summary>
internal static class TtsPlayer
{
    private const int PoolSize = 3;
    private const double FadeSeconds = 0.15;
    private const int MaxQueuedPerLine = 2;

    private static readonly List<AudioStreamPlayer> Pool = new();
    private static readonly List<byte[]> Pending = new();
    private static Node? _host;
    private static AudioStreamPlayer? _current;
    private static string _activeLineId = "";
    private static int _cursor;

    /// <summary>
    /// Plays one MP3 segment. <paramref name="lineId"/> groups segments that belong to the same
    /// chat line; volume comes from the voice bus, never from a per-call value.
    /// </summary>
    public static void Play(byte[] mp3, bool interrupt, string? lineId)
    {
        if (mp3.Length == 0)
            return;

        if (!MainThread.IsMainThread)
        {
            MainThread.Post(() => Play(mp3, interrupt, lineId));
            return;
        }

        var id = lineId ?? "";
        var sameLine = string.Equals(id, _activeLineId, StringComparison.Ordinal);

        if (!sameLine)
        {
            Pending.Clear();
            if (interrupt)
                FadeOutCurrent();
            _activeLineId = id;
        }
        else if (AnyPlaying())
        {
            // Same line, previous segment still talking: keep the order instead of overlapping.
            if (Pending.Count < MaxQueuedPerLine)
                Pending.Add(mp3);
            return;
        }

        PlayNow(mp3);
    }

    /// <summary>Fades out everything currently speaking (mute toggle, run end, chat panel gone).</summary>
    public static void StopAll()
    {
        if (!MainThread.IsMainThread)
        {
            MainThread.Post(StopAll);
            return;
        }

        Pending.Clear();
        _activeLineId = "";

        foreach (var player in Pool)
        {
            if (GodotObject.IsInstanceValid(player) && player.Playing)
                FadeAndStop(player);
        }

        _current = null;
    }

    private static void PlayNow(byte[] mp3)
    {
        AudioStreamMP3? stream;
        try
        {
            stream = AudioStreamMP3.LoadFromBuffer(mp3);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[VOICE] MP3 解码失败: {ex.Message}");
            return;
        }

        if (stream == null)
        {
            MainFile.Logger.Warn("[VOICE] MP3 解码返回空流");
            return;
        }

        var host = ResolveHost();
        if (host == null)
            return;

        // Create/fix the bus first: assigning a bus name that does not exist yet makes Godot log a
        // "bus not found" error, and the player would start on the wrong bus for one frame.
        var bus = TtsAudioBus.Ensure(AiSettingsStore.Current.VoiceVolumeDb);

        EnsurePool(host);

        var player = Acquire(host);
        player.Stop();
        player.Bus = bus;
        player.VolumeDb = TtsAudioBus.PlayerVolumeDb;
        player.Stream = stream;
        player.Play();
        _current = player;

        MainFile.Logger.Info(
            $"[VOICE] 播放 {stream.GetLength():0.0}s (bus {player.Bus}, {player.VolumeDb:+0;-0;0} dB)");
    }

    /// <summary>Starts the next queued segment of the same line when a player reaches the end.</summary>
    private static void OnPlayerFinished()
    {
        if (Pending.Count == 0)
            return;

        var next = Pending[0];
        Pending.RemoveAt(0);
        PlayNow(next);
    }

    private static bool AnyPlaying()
    {
        foreach (var player in Pool)
        {
            if (GodotObject.IsInstanceValid(player) && player.Playing)
                return true;
        }

        return false;
    }

    private static void FadeOutCurrent()
    {
        var player = _current;
        _current = null;
        if (player == null || !GodotObject.IsInstanceValid(player) || !player.Playing)
            return;

        FadeAndStop(player);
    }

    private static void FadeAndStop(AudioStreamPlayer player)
    {
        try
        {
            var tween = player.CreateTween();
            tween.TweenProperty(player, "volume_db", -50.0f, FadeSeconds);
            tween.TweenCallback(Callable.From(() =>
            {
                if (GodotObject.IsInstanceValid(player))
                    player.Stop();
            }));
        }
        catch
        {
            // No tween (node left the tree): just stop.
            try { player.Stop(); } catch { /* ignored */ }
        }
    }

    private static AudioStreamPlayer Acquire(Node host)
    {
        foreach (var player in Pool)
        {
            if (!GodotObject.IsInstanceValid(player))
                continue;
            if (!player.Playing)
                return player;
        }

        if (Pool.Count == 0)
            return CreatePlayer(host);

        // Everything is busy: reuse the round-robin slot (the interrupt path already faded the
        // previous line out, so this normally means "several lines queued back to back").
        var reused = Pool[_cursor % Pool.Count];
        _cursor++;
        if (GodotObject.IsInstanceValid(reused))
            return reused;

        return CreatePlayer(host);
    }

    private static void EnsurePool(Node host)
    {
        if (_host != null && GodotObject.IsInstanceValid(_host) && _host.IsInsideTree())
            return;

        _host = host;
        Pool.RemoveAll(player => !GodotObject.IsInstanceValid(player));

        foreach (var player in Pool)
        {
            if (player.GetParent() == null && GodotObject.IsInstanceValid(player))
            {
                try { host.AddChild(player); }
                catch { /* already elsewhere */ }
            }
        }
    }

    private static AudioStreamPlayer CreatePlayer(Node host)
    {
        var player = new AudioStreamPlayer
        {
            Name = $"AiTeammateVoice{Pool.Count}",
            Bus = TtsAudioBus.BusName,
            ProcessMode = Node.ProcessModeEnum.Always
        };

        player.Finished += OnPlayerFinished;

        try { host.AddChild(player); }
        catch (Exception ex) { MainFile.Logger.Warn($"[VOICE] 挂载播放器失败: {ex.Message}"); }

        Pool.Add(player);
        return player;
    }

    private static Node? ResolveHost()
    {
        if (_host != null && GodotObject.IsInstanceValid(_host) && _host.IsInsideTree())
            return _host;

        var game = NGame.Instance;
        if (game != null && GodotObject.IsInstanceValid(game) && game.IsInsideTree())
        {
            _host = game;
            return game;
        }

        if (Engine.GetMainLoop() is SceneTree tree && tree.Root != null && GodotObject.IsInstanceValid(tree.Root))
        {
            _host = tree.Root;
            return tree.Root;
        }

        return null;
    }
}
