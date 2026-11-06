using System.Text.Json.Nodes;

namespace AiTeammate.AiTeammateCode.Agent;

/// <summary>
/// Outcome of the last answer the broker accepted: which actions the validator kept, which ones it
/// dropped and why. dsh never saw this before — <c>sts2_decide</c> answered "ok" even when every card
/// name was wrong, and the next pass just showed the same hand again.
/// 上一份决策的校验结果（哪些动作没执行、为什么）。
/// </summary>
internal static class AgentDecisionFeedback
{
    private static readonly Lock Sync = new();
    private static string _id = "";
    private static string _kind = "";
    private static readonly List<string> Requested = new();
    private static readonly List<string> Dropped = new();
    private static readonly List<string> Resolved = new();
    private static int _valid;
    private static DateTimeOffset _recordedAt = DateTimeOffset.UtcNow;

    public static void Record(string? id, string? kind, IEnumerable<string>? requested,
        IEnumerable<string>? dropped, int valid, IEnumerable<string>? resolved = null)
    {
        lock (Sync)
        {
            _id = id ?? "";
            _kind = kind ?? "";
            Requested.Clear();
            if (requested != null)
                Requested.AddRange(requested);
            Dropped.Clear();
            if (dropped != null)
                Dropped.AddRange(dropped);
            Resolved.Clear();
            if (resolved != null)
                Resolved.AddRange(resolved);
            _valid = valid;
            _recordedAt = DateTimeOffset.UtcNow;
        }
    }

    public static JsonObject? SnapshotJson()
    {
        lock (Sync)
        {
            if (_id.Length == 0 && Requested.Count == 0 && Dropped.Count == 0)
                return null;

            var requested = new JsonArray();
            foreach (var item in Requested)
                requested.Add(item);
            var dropped = new JsonArray();
            foreach (var item in Dropped)
                dropped.Add(item);
            var resolved = new JsonArray();
            foreach (var item in Resolved)
                resolved.Add(item);

            return new JsonObject
            {
                ["id"] = _id,
                ["kind"] = _kind,
                ["requested"] = requested,
                ["dropped"] = dropped,
                // "Card->Name[#2]": the exact creature the plan locked in, so dsh can see whether the
                // hit landed on the twin it meant. 解析到的具体实例，供 dsh 自检。
                ["resolved"] = resolved,
                ["valid"] = _valid,
                // Informational only; AgentSnapshotBuilder strips it back out before hashing the
                // token, so the countdown cannot make the token churn.
                ["age_seconds"] = Math.Round((DateTimeOffset.UtcNow - _recordedAt).TotalSeconds, 1)
            };
        }
    }

    public static void Reset()
    {
        lock (Sync)
        {
            _id = "";
            _kind = "";
            Requested.Clear();
            Dropped.Clear();
            Resolved.Clear();
            _valid = 0;
        }
    }
}
