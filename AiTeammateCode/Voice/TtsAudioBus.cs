using Godot;

namespace AiTeammate.AiTeammateCode.Voice;

/// <summary>
/// Dedicated Godot audio bus for the AI voice (<c>AiVoice</c> → <c>Master</c>), created on first use.
///
/// Why not the game's <c>SFX</c> bus: the base game plays its own sound through FMOD, so the Godot
/// <c>SFX</c> bus only carries debug sounds — but the game maps the player's "sound effects" slider
/// onto that bus with a <c>linear²</c> curve (<c>NDebugAudioManager.SetSfxAudioVolume</c>), and the
/// master slider squares the master bus the same way. At the very common 50 % / 70 % settings the
/// two together remove roughly 18 dB, which is why the first cut of the voice was barely audible.
///
/// The dedicated bus keeps the voice out of that chain and carries the mod's own volume instead.
/// A positive offset is expected rather than cosmetic: MiniMax output measures about
/// <c>-19.5 LUFS</c> integrated / <c>-5.3 dBFS</c> peak, i.e. mastered for a full mix, not for a
/// dry hit under game audio.
///
/// The gain is an <see cref="AudioEffectAmplify"/> placed **first** in the effect chain, followed by
/// a hard limiter. Keeping the boost inside the chain (instead of on the bus volume) makes the order
/// deterministic — boost → limit — so the limiter always sees what the boost produced.
/// 独立语音总线：绕开游戏「音效」音量的线性² 衰减；增益做在效果链最前面的 Amplify 上，
/// 再串硬限幅，保证顺序固定为 放大 → 限幅。
/// </summary>
internal static class TtsAudioBus
{
    public const string BusName = "AiVoice";

    /// <summary>Slider bounds; positive values are the interesting half. 音量范围（dB）。</summary>
    public const int MinVolumeDb = -24;
    public const int MaxVolumeDb = 24;

    /// <summary>Amplify stops at +24 dB, so the setting sits inside its range. 增益上限。</summary>
    private const float AmplifyMaxDb = 24f;

    private const float LimiterCeilingDb = -0.5f;
    private const float LogThresholdDb = 0.5f;

    private static AudioEffectAmplify? _amplify;
    private static float _appliedDb = float.NaN;
    private static bool _degraded;

    /// <summary>
    /// Offset the players must add on top of the bus. Zero while the dedicated bus works; in the
    /// (unlikely) degraded path it cancels the fallback bus's own attenuation so the number in the
    /// settings still means what it says. 播放器需要额外叠加的音量偏移，正常为 0。
    /// </summary>
    public static float PlayerVolumeDb { get; private set; }

    /// <summary>
    /// Ensures the bus exists and applies <paramref name="volumeDb"/>; returns the bus name the
    /// players should use. Main thread only (AudioServer is not thread safe).
    /// </summary>
    public static string Ensure(int volumeDb)
    {
        var db = Math.Clamp(volumeDb, MinVolumeDb, MaxVolumeDb);

        if (_degraded)
            return ApplyFallback(db);

        try
        {
            var index = AudioServer.GetBusIndex(BusName);
            if (index < 0)
            {
                var added = AudioServer.BusCount;
                AudioServer.AddBus();
                AudioServer.SetBusName(added, BusName);
                AudioServer.SetBusSend(added, "Master");

                // Never trust the add blindly: a bus we cannot look up by name would make every
                // later call append yet another orphan bus.
                index = AudioServer.GetBusIndex(BusName);
                if (index < 0)
                    throw new InvalidOperationException("总线名注册失败（GetBusIndex 仍为 -1）");

                // Gain lives on the first effect, so the bus volume itself stays neutral.
                AudioServer.SetBusVolumeDb(index, 0f);

                _amplify = new AudioEffectAmplify { VolumeDb = Math.Clamp(db, MinVolumeDb, AmplifyMaxDb) };
                AudioServer.AddBusEffect(index, _amplify);
                _appliedDb = db;

                var dynamics = TryAddLimiter(index);
                MainFile.Logger.Info(
                    $"[VOICE] 建立独立语音总线 {BusName} (index={index}, 增益 {db:+0;-0;0} dB, " +
                    $"effects=amplify{dynamics}, master={BusDb("Master"):0.0} dB, " +
                    $"旧 SFX 总线={BusDb("SFX"):0.0} dB 不再叠加)");
            }
            else if (_amplify == null || !GodotObject.IsInstanceValid(_amplify))
            {
                // Bus already there (e.g. another mod, or an earlier session kept the layout): use
                // the bus volume instead, since we cannot safely reorder its effects.
                _amplify = null;
                if (float.IsNaN(_appliedDb) || Math.Abs(db - _appliedDb) >= LogThresholdDb)
                {
                    AudioServer.SetBusVolumeDb(index, db);
                    _appliedDb = db;
                    MainFile.Logger.Info($"[VOICE] 语音音量 {db:+0;-0;0} dB（复用的 {BusName} 总线）");
                }

                PlayerVolumeDb = 0f;
                return BusName;
            }

            if (_amplify != null && (float.IsNaN(_appliedDb) || Math.Abs(db - _appliedDb) >= LogThresholdDb))
            {
                _amplify.VolumeDb = Math.Clamp(db, MinVolumeDb, AmplifyMaxDb);
                _appliedDb = db;
                MainFile.Logger.Info($"[VOICE] 语音音量 {db:+0;-0;0} dB（独立通道，不受游戏音效音量影响）");
            }

            PlayerVolumeDb = 0f;
            return BusName;
        }
        catch (Exception ex)
        {
            if (!_degraded)
            {
                _degraded = true;
                MainFile.Logger.Warn($"[VOICE] 独立语音总线不可用，退回游戏总线并补偿衰减: {ex.Message}");
            }

            return ApplyFallback(db);
        }
    }

    private static string ApplyFallback(int db)
    {
        try
        {
            var sfx = AudioServer.GetBusIndex("SFX");
            var bus = sfx >= 0 ? "SFX" : "Master";
            var index = sfx >= 0 ? sfx : AudioServer.GetBusIndex("Master");
            var busDb = index >= 0 ? AudioServer.GetBusVolumeDb(index) : 0f;
            PlayerVolumeDb = db - busDb;
            return bus;
        }
        catch
        {
            PlayerVolumeDb = db;
            return "Master";
        }
    }

    private static string TryAddLimiter(int index)
    {
        // Amplify already pushed the level up; TTS peaks land near 0 dBFS at +6 dB. A hard limiter
        // keeps those peaks inside the mix without the pumping a compressor would add, and it is
        // the only sensible safety net when the player cranks the slider to +24 dB.
        try
        {
            AudioServer.AddBusEffect(index, new AudioEffectHardLimiter
            {
                CeilingDb = LimiterCeilingDb,
                PreGainDb = 0f,
                Release = 0.1f
            });
            return "+limiter";
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[VOICE] 限幅器不可用: {ex.Message}");
            return "+none";
        }
    }

    private static float BusDb(string busName)
    {
        var index = AudioServer.GetBusIndex(busName);
        return index >= 0 ? AudioServer.GetBusVolumeDb(index) : 0f;
    }
}
