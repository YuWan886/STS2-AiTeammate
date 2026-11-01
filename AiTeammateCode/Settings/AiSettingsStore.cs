using STS2RitsuLib;
using STS2RitsuLib.Data;
using STS2RitsuLib.Utils.Persistence;

namespace AiTeammate.AiTeammateCode.Settings;

/// <summary>
/// Registration and live access for <see cref="AiSettings"/> through RitsuLib's ModDataStore.
/// All services read <see cref="Current"/> at use time so settings take effect immediately.
/// </summary>
public static class AiSettingsStore
{
    public const string DataKey = "settings";
    public const string FileName = "settings.json";

    private static int _migrated;

    /// <summary>The live settings instance (mutated in place by the settings page). 当前设置实例。</summary>
    public static AiSettings Current
    {
        get
        {
            var settings = RitsuLibFramework.GetDataStore(MainFile.ModId).Get<AiSettings>(DataKey);
            EnsureMigrated(settings);
            return settings;
        }
    }

    /// <summary>Registers the data slot. Call inside <c>RitsuLibFramework.BeginModDataRegistration</c>.</summary>
    public static void RegisterData()
    {
        ModDataStore.For(MainFile.ModId).Register(
            key: DataKey,
            fileName: FileName,
            scope: SaveScope.Global,
            defaultFactory: static () => new AiSettings(),
            autoCreateIfMissing: true);
    }

    /// <summary>
    /// Applies pending default migrations. Called once from <c>MainFile</c> right after the
    /// registration block (never from inside it) so the store is fully usable; a failure is
    /// retried by the next <see cref="Current"/> read.
    /// 启动时执行一次设置迁移。
    /// </summary>
    public static void Migrate()
    {
        try
        {
            EnsureMigrated(Current);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[SETTINGS] 读取设置做迁移失败: {ex.Message}");
        }
    }

    /// <summary>Persists the live settings. 持久化当前设置。</summary>
    public static void Save()
    {
        RitsuLibFramework.GetDataStore(MainFile.ModId).Save(DataKey);
    }

    /// <summary>
    /// One-off default migrations for files written by an older version. A changed default never
    /// reaches an existing <c>settings.json</c> (the old value is already stored), so anything that
    /// has to ship to current players goes through here; the schema version is then bumped and
    /// saved, so a value the player picks afterwards is never overwritten again.
    /// 旧版本设置文件的一次性默认值迁移。
    /// </summary>
    private static void EnsureMigrated(AiSettings settings)
    {
        if (Volatile.Read(ref _migrated) == 1)
            return;

        try
        {
            if (settings.SettingsSchemaVersion >= AiSettings.CurrentSchemaVersion)
            {
                Volatile.Write(ref _migrated, 1);
                return;
            }

            var changes = new List<string>();

            // v1 → v2: the voice left the game's SFX bus (which the game attenuates by slider²,
            // ≈ -18 dB at common settings) for its own bus, so the old 0 dB default was inaudible.
            if (settings.VoiceVolumeDb == 0)
            {
                settings.VoiceVolumeDb = new AiSettings().VoiceVolumeDb;
                changes.Add($"VoiceVolumeDb 0 → {settings.VoiceVolumeDb}");
            }

            settings.SettingsSchemaVersion = AiSettings.CurrentSchemaVersion;
            Save();
            Volatile.Write(ref _migrated, 1);

            if (changes.Count > 0)
                MainFile.Logger.Info(
                    $"[SETTINGS] 设置已迁移到 v{AiSettings.CurrentSchemaVersion}: {string.Join(", ", changes)}");
        }
        catch (Exception ex)
        {
            // Left un-migrated on purpose: the next read retries instead of silently keeping the
            // old default forever.
            MainFile.Logger.Warn($"[SETTINGS] 设置迁移失败: {ex.Message}");
        }
    }
}
