using System.Text.Json;

namespace AiTeammate.AiTeammateCode.Compat;

/// <summary>Tiny JSON codec for out-of-combat AI choices shared between peers.</summary>
internal static class AiChoiceCodec
{
    internal sealed class ChoiceDto
    {
        public int index { get; set; } = -1;
        public List<int> indices { get; set; } = new();
        public string reason { get; set; } = "";

        /// <summary>Stable option id (rest sites) so a peer can map the choice without trusting order.</summary>
        public string id { get; set; } = "";
    }

    public static string Serialize(int index, List<int>? indices = null, string reason = "", string id = "")
    {
        try
        {
            return JsonSerializer.Serialize(new ChoiceDto
            {
                index = index,
                indices = indices ?? new List<int>(),
                reason = reason ?? "",
                id = id ?? ""
            });
        }
        catch
        {
            return $"{{\"index\":{index}}}";
        }
    }

    public static ChoiceDto? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<ChoiceDto>(json);
        }
        catch
        {
            return null;
        }
    }

    public static string Key(string kind, params object[] parts) =>
        kind + ":" + string.Join(":", parts);
}
