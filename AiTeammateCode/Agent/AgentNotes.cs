using System.Text.Json;
using System.Text.Json.Nodes;
using Godot;

namespace AiTeammate.AiTeammateCode.Agent;

/// <summary>
/// Free-form notes the agent writes with <c>sts2_note</c> (deck plan, boss plan, relic to buy, ...).
///
/// They are injected into every decision prompt and returned in the snapshot, which is what keeps a
/// long run's plan alive after dsh's own conversation context gets compacted. The file is keyed by
/// the run seed, so notes survive a game restart of the same run and are cleared when a new run
/// starts. 外部 Agent 自己写的长期笔记（按种子归属，换局自动清空）。
/// </summary>
internal static class AgentNotes
{
    private const int MaxNotes = 20;
    private const int MaxNoteLength = 300;
    private const string FileName = "agent_notes.json";

    private static readonly Lock Sync = new();
    private static readonly List<string> Entries = new();
    private static bool _loaded;
    private static string _runKey = "";
    private static string? _directory;

    private static string Directory => _directory ??=
        Path.Combine(ProjectSettings.GlobalizePath(ModPaths.RootDir), "agent");

    private static string FilePath => Path.Combine(Directory, FileName);

    /// <summary>Ties the notes to one run; a different seed clears them. Called from the snapshot.</summary>
    public static void EnsureRun(string? runKey)
    {
        var key = (runKey ?? "").Trim();
        if (key.Length == 0)
            return;

        lock (Sync)
        {
            EnsureLoadedLocked();
            if (string.Equals(_runKey, key, StringComparison.Ordinal))
                return;

            // Notes from a finished/other run must not leak into this one. The first key we ever see
            // is simply adopted (legacy file without a run key keeps its notes).
            if (_runKey.Length > 0)
                Entries.Clear();

            _runKey = key;
            SaveLocked();
        }
    }

    /// <summary>Notes as a JSON array (empty array when there are none).</summary>
    public static JsonArray SnapshotJson()
    {
        lock (Sync)
        {
            EnsureLoadedLocked();
            var array = new JsonArray();
            foreach (var entry in Entries)
                array.Add(entry);
            return array;
        }
    }

    /// <summary>Notes as prompt lines, or an empty string when there are none.</summary>
    public static string SnapshotForPrompt()
    {
        lock (Sync)
        {
            EnsureLoadedLocked();
            if (Entries.Count == 0)
                return "";

            var lines = new List<string>(Entries.Count);
            foreach (var entry in Entries)
                lines.Add("- " + entry);
            return string.Join(System.Environment.NewLine, lines);
        }
    }

    /// <summary>Adds one note (bounded and de-duplicated). Safe from the bridge thread.</summary>
    public static (bool Ok, string Message) Add(string? text)
    {
        var note = (text ?? "").Trim();
        if (note.Length == 0)
            return (false, "Missing 'text'. Pass the note to remember (at most 300 characters).");

        if (note.Length > MaxNoteLength)
            note = note[..MaxNoteLength];

        lock (Sync)
        {
            EnsureLoadedLocked();
            if (Entries.Contains(note))
                return (true, $"That note is already saved ({Entries.Count} note(s) for this run).");

            Entries.Add(note);
            while (Entries.Count > MaxNotes)
                Entries.RemoveAt(0);

            SaveLocked();
            return (true, $"Note saved ({Entries.Count}/{MaxNotes}). It will be shown in later decision prompts.");
        }
    }

    /// <summary>Clears the notes entirely (kept for explicit resets).</summary>
    public static void Reset()
    {
        lock (Sync)
        {
            Entries.Clear();
            _runKey = "";
            _loaded = true;
            SaveLocked();
        }
    }

    private static void EnsureLoadedLocked()
    {
        if (_loaded)
            return;
        _loaded = true;

        try
        {
            if (!File.Exists(FilePath))
                return;

            var json = File.ReadAllText(FilePath);
            var parsed = JsonNode.Parse(json);
            JsonArray? array = null;
            switch (parsed)
            {
                case JsonArray direct:
                    array = direct; // legacy / hand-edited format
                    break;
                case JsonObject obj:
                    _runKey = obj["run"]?.GetValue<string>() ?? "";
                    array = obj["notes"] as JsonArray;
                    break;
            }

            if (array == null)
                return;

            foreach (var node in array)
            {
                var note = node?.GetValue<string>()?.Trim();
                if (!string.IsNullOrEmpty(note) && !Entries.Contains(note))
                    Entries.Add(note);
            }

            while (Entries.Count > MaxNotes)
                Entries.RemoveAt(0);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Agent] notes load failed: {ex.Message}");
        }
    }

    private static void SaveLocked()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var array = new JsonArray();
            foreach (var entry in Entries)
                array.Add(entry);

            var json = new JsonObject
            {
                ["run"] = _runKey,
                ["notes"] = array
            };
            File.WriteAllText(FilePath, json.ToJsonString(AgentJson.Options));
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Agent] notes save failed: {ex.Message}");
        }
    }
}
