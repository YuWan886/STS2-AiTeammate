using System.Security.Cryptography;
using System.Text;
using Godot;

namespace AiTeammate.AiTeammateCode.Voice;

/// <summary>
/// On-disk cache for synthesized lines (<c>user://ai_teammate/voice/&lt;sha1&gt;.mp3</c>). A hit costs
/// nothing and starts instantly, which matters because repeated lines ("又见面了。") are common and
/// MiniMax bills per character.
/// 语音缓存：同一句 + 同一音色只合成一次。
/// </summary>
internal static class TtsAudioCache
{
    private const string Extension = ".mp3";
    private static readonly object Sync = new();

    private static string DirectoryPath => ProjectSettings.GlobalizePath(ModPaths.VoiceDir);

    /// <summary>Stable key over every parameter that changes the audio (text included).</summary>
    public static string Key(TtsSynthOptions options, string text)
    {
        var raw = string.Join('|',
            options.Model, options.VoiceId, options.SpeedPercent, options.Pitch,
            options.Emotion, options.LanguageBoost, text);
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static bool TryGet(string key, out byte[] data)
    {
        data = [];
        try
        {
            var path = PathFor(key);
            lock (Sync)
            {
                if (!File.Exists(path))
                    return false;

                data = File.ReadAllBytes(path);
                // Touch for LRU ordering; a failure here must not lose the cache hit.
                try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); }
                catch { /* ignored */ }
            }

            return data.Length > 0;
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[VOICE] 读取语音缓存失败: {ex.Message}");
            return false;
        }
    }

    public static void Put(string key, byte[] data, int maxFiles, int maxMegabytes)
    {
        if (data.Length == 0)
            return;

        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(DirectoryPath);
                File.WriteAllBytes(PathFor(key), data);
                TrimLocked(Math.Clamp(maxFiles, 0, 5000), Math.Clamp(maxMegabytes, 1, 4096) * 1024L * 1024L);
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[VOICE] 写入语音缓存失败: {ex.Message}");
        }
    }

    public static (int Files, long Bytes) Stats()
    {
        try
        {
            lock (Sync)
            {
                var files = Enumerate();
                return (files.Count, files.Sum(f => f.Size));
            }
        }
        catch
        {
            return (0, 0);
        }
    }

    public static void Clear()
    {
        try
        {
            lock (Sync)
            {
                foreach (var file in Enumerate())
                {
                    try { File.Delete(file.Path); }
                    catch { /* best effort */ }
                }
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[VOICE] 清空语音缓存失败: {ex.Message}");
        }
    }

    private static string PathFor(string key) => Path.Combine(DirectoryPath, key + Extension);

    private static List<(string Path, long Size, DateTime Written)> Enumerate()
    {
        var dir = DirectoryPath;
        var files = new List<(string Path, long Size, DateTime Written)>();
        if (!Directory.Exists(dir))
            return files;

        foreach (var path in Directory.EnumerateFiles(dir, "*" + Extension))
        {
            try
            {
                var info = new FileInfo(path);
                files.Add((path, info.Length, info.LastWriteTimeUtc));
            }
            catch
            {
                // A file that vanished between enumeration and stat is simply skipped.
            }
        }

        return files;
    }

    /// <summary>Deletes the oldest files until both the count and the size limits hold.</summary>
    private static void TrimLocked(int maxFiles, long maxBytes)
    {
        var files = Enumerate();
        if (files.Count == 0)
            return;

        var totalBytes = files.Sum(f => f.Size);
        var totalFiles = files.Count;
        if (totalFiles <= maxFiles && totalBytes <= maxBytes)
            return;

        foreach (var file in files.OrderBy(f => f.Written))
        {
            if (totalFiles <= maxFiles && totalBytes <= maxBytes)
                break;

            try
            {
                File.Delete(file.Path);
                totalFiles--;
                totalBytes -= file.Size;
            }
            catch
            {
                // Keep counting it: the next trim pass will retry.
            }
        }
    }
}
