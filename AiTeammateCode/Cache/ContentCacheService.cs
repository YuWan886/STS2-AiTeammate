using System.Text.Encodings.Web;
using System.Text.Json;
using Godot;

namespace AiTeammate.AiTeammateCode.Cache;

public class ContentCacheService
{
    // v4: card costs use the upgrade-adjusted value (GetWithModifiers) instead of the printed
    // Canonical cost, so upgradeCostText is finally correct; old caches must be regenerated.
    private const int CacheSchemaVersion = 4;
    private const string IndexPath = "user://ai_teammate/cache/content_index.json";
    private const string FullPath = "user://ai_teammate/cache/content_full.json";
    private const string CacheDir = ModPaths.CacheDir;

    public ContentIndex? Index { get; private set; }
    public ContentFull? Full { get; private set; }
    public bool IsLoaded { get; private set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public void Initialize()
    {
        if (!ModPaths.EnsureDir(CacheDir))
            return;

        var currentVersion = GetCacheVersion();

        if (Godot.FileAccess.FileExists(IndexPath) && Godot.FileAccess.FileExists(FullPath))
        {
            try
            {
                using var indexFile = Godot.FileAccess.Open(IndexPath, Godot.FileAccess.ModeFlags.Read);
                Index = JsonSerializer.Deserialize<ContentIndex>(indexFile.GetAsText(), JsonOptions);

                using var fullFile = Godot.FileAccess.Open(FullPath, Godot.FileAccess.ModeFlags.Read);
                Full = JsonSerializer.Deserialize<ContentFull>(fullFile.GetAsText(), JsonOptions);

                if (Index?.GameVersion == currentVersion && Full?.GameVersion == currentVersion)
                {
                    IsLoaded = true;
                    MainFile.Logger.Info($"Content cache loaded (version: {currentVersion})");
                    return;
                }

                if (!Settings.AiSettingsStore.Current.AutoRefreshCache)
                {
                    // The player disabled automatic regeneration. Keep the stale cache rather than
                    // blocking startup; the settings page still offers a manual rebuild.
                    IsLoaded = Index != null && Full != null;
                    MainFile.Logger.Warn($"Content cache version mismatch ({Index?.GameVersion}/{Full?.GameVersion} != {currentVersion})，自动刷新已关闭，继续使用旧缓存");
                    return;
                }
            }
            catch (Exception ex)
            {
                MainFile.Logger.Warn($"Cache files unreadable, will regenerate: {ex.Message}");
            }
        }

        if (!Settings.AiSettingsStore.Current.AutoRefreshCache && Index != null && Full != null)
        {
            IsLoaded = true;
            return;
        }

        // Generation is deliberately left to EnsureLoaded(): Initialize() also runs from the mod
        // settings page while the game is still loading mods, when ModelDb (the source of every card
        // and character) does not exist yet.
    }

    /// <summary>
    /// Regenerates the content index when it is not usable yet. Call once the game models are
    /// available (NGame ready). Safe to call repeatedly; a failed generation is retried next time.
    /// </summary>
    public void EnsureLoaded()
    {
        if (IsLoaded)
            return;

        RegenerateCache();
    }

    public void RegenerateCache(string? gameVersion = null)
    {
        var version = gameVersion ?? GetCacheVersion();
        MainFile.Logger.Info($"Regenerating content cache (version: {version})...");

        try
        {
            var cardGen = new CardCacheGenerator();
            var relicGen = new RelicCacheGenerator();
            var potionGen = new PotionCacheGenerator();
            var enchantGen = new EnchantmentCacheGenerator();

            Index = new ContentIndex
            {
                GameVersion = version,
                Cards = cardGen.GenerateIndex(),
                Relics = relicGen.GenerateIndex(),
                Potions = potionGen.GenerateIndex(),
                Enchants = enchantGen.GenerateIndex()
            };

            Full = new ContentFull
            {
                GameVersion = version,
                Cards = cardGen.GenerateFull(),
                Relics = relicGen.GenerateFull(),
                Potions = potionGen.GenerateFull(),
                Enchants = enchantGen.GenerateFull()
            };

            SaveToDisk();
            IsLoaded = true;
            MainFile.Logger.Info($"Content cache regenerated: {Index.Cards.Count} cards, {Index.Relics.Count} relics, {Index.Potions.Count} potions, {Index.Enchants.Count} enchants");
        }
        catch (Exception ex)
        {
            // Runs during NGame startup and from the settings page: one broken modded card/model
            // must not take down the game. The AI keeps working with whatever cache is available.
            IsLoaded = Index != null && Full != null;
            MainFile.Logger.Error($"Content cache regeneration failed: {ex}");
        }
    }

    public void SaveToDisk()
    {
        if (!ModPaths.EnsureDir(CacheDir))
            return;

        if (Index != null)
        {
            using var file = Godot.FileAccess.Open(IndexPath, Godot.FileAccess.ModeFlags.Write);
            file.StoreString(JsonSerializer.Serialize(Index, JsonOptions));
        }
        if (Full != null)
        {
            using var file = Godot.FileAccess.Open(FullPath, Godot.FileAccess.ModeFlags.Write);
            file.StoreString(JsonSerializer.Serialize(Full, JsonOptions));
        }
    }

    private static string GetCacheVersion()
    {
        // Assembly.Version is 0.1.0.0 for every game build; the module MVID changes per build,
        // so this correctly invalidates the cache after a game update.
        var assembly = typeof(MegaCrit.Sts2.Core.Nodes.NGame).Assembly;
        var buildId = assembly.ManifestModule.ModuleVersionId.ToString("N");
        return $"{buildId}-cache{CacheSchemaVersion}";
    }
}
