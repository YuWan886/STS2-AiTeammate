using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using STS2RitsuLib;
using AiTeammate.AiTeammateCode.Bootstrap;
using AiTeammate.AiTeammateCode.Compat;
using AiTeammate.AiTeammateCode.Personality;
using AiTeammate.AiTeammateCode.Settings;

namespace AiTeammate.AiTeammateCode;

/// <summary>
/// Content-variant entry point. The loader (AiTeammate.dll) invokes this after picking the
/// variant that matches the running game version.
/// </summary>
[ModInitializer(nameof(Initialize))]
public static class MainFile
{
    public const string ModId = "AiTeammate";
    public static Logger Logger { get; } = new(ModId, LogType.Generic);

    public static void Initialize()
    {
        InitializeSettings();
        InstallPatches();

        // The mod is loaded after NGame._Ready has already run, so the runtime stack cannot wait for
        // that patch: the bootstrap polls for a usable game root and initializes it itself.
        ModBootstrap.Start();
    }

    private static void InitializeSettings()
    {
        try
        {
            using (RitsuLibFramework.BeginModDataRegistration(ModId))
            {
                AiSettingsStore.RegisterData();
                PersonalityStore.RegisterData();
            }

            // Changed defaults never reach an existing settings.json, so old files get one pass of
            // migrations (and a schema-version bump) right after the store becomes usable.
            AiSettingsStore.Migrate();

            // Built-in personality text is localized, so seeding waits until LocManager exists
            // (NGame ready or the settings page build); mod initialization runs before it.
            AiSettingsPage.Register();

            Logger.Info("设置初始化完成（人格数据将在游戏就绪时载入）");
        }
        catch (Exception ex)
        {
            Logger.Error($"[Settings] 初始化失败（Mod 仍会尝试加载，但设置不可用）: {ex}");
        }
    }

    private static void InstallPatches()
    {
        try
        {
            SafePatchInstaller.InstallAll(new Harmony(ModId));
        }
        catch (Exception ex)
        {
            Logger.Error($"[Patch] 补丁安装失败: {ex}");
        }
    }
}
