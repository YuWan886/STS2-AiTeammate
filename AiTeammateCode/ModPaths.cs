using Godot;

namespace AiTeammate.AiTeammateCode;

/// <summary>
/// Shared <c>user://</c> paths for the mod. Everything lives under <see cref="RootDir"/>, which has to
/// be created recursively: Godot's <c>MakeDirAbsolute</c> refuses to create a child of a missing
/// parent, so creating <c>user://ai_teammate/cache</c> on a fresh install used to fail with
/// <c>CantCreate</c> and the cache, session log and long-term memories were silently never written.
/// </summary>
internal static class ModPaths
{
    public const string RootDir = "user://ai_teammate";
    public const string LogDir = RootDir + "/logs";
    public const string CacheDir = RootDir + "/cache";
    public const string MemoryDir = RootDir + "/memories";

    /// <summary>MiniMax TTS audio cache (one file per unique line/voice combination). TTS 语音缓存。</summary>
    public const string VoiceDir = RootDir + "/voice";

    /// <summary>Creates <paramref name="path"/> and any missing parents. True when it exists afterwards.</summary>
    public static bool EnsureDir(string path)
    {
        if (DirAccess.DirExistsAbsolute(path))
            return true;

        var err = DirAccess.MakeDirRecursiveAbsolute(path);
        if (err != Error.Ok)
            MainFile.Logger.Warn($"[Storage] 无法创建目录 {path} (Error: {err})");

        return err == Error.Ok;
    }
}
