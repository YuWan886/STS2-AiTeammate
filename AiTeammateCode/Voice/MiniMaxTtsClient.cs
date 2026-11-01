using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiTeammate.AiTeammateCode.Voice;

/// <summary>Result of one synthesis request: MP3 bytes plus the metadata the UI/cost layer needs.</summary>
public sealed record TtsSynthesisResult(bool Ok, byte[]? Audio, int AudioLengthMs, int UsageCharacters, string Error)
{
    public static TtsSynthesisResult Success(byte[] audio, int lengthMs, int usage) =>
        new(true, audio, lengthMs, usage, "");

    public static TtsSynthesisResult Failure(string error) => new(false, null, 0, 0, error);
}

/// <summary>One selectable voice reported by <c>POST /v1/get_voice</c>.</summary>
public sealed record TtsVoiceInfo(string VoiceId, string VoiceName);

/// <summary>Everything one synthesis request needs, resolved from settings at call time.</summary>
public sealed record TtsSynthOptions(
    string BaseUrl,
    string ApiKey,
    string Model,
    string VoiceId,
    int SpeedPercent,
    int Pitch,
    string Emotion,
    string LanguageBoost);

/// <summary>
/// Thin MiniMax T2A (text-to-speech) client. One POST for the audio, one POST for the voice list;
/// no SDK, no streaming. MiniMax answers HTTP 200 even for failures, so every response is checked
/// through <c>base_resp.status_code</c>.
/// MiniMax 同步语音合成客户端（非流式）。
/// </summary>
public static class MiniMaxTtsClient
{
    public const string DefaultBaseUrl = "https://api.minimax.cn";
    public const string DefaultModel = "speech-2.8-turbo";
    public const string DefaultVoiceId = "male-qn-qingse";

    /// <summary>Models the settings page offers; the text field still accepts anything newer.</summary>
    public static readonly string[] KnownModels =
    [
        "speech-2.8-turbo", "speech-2.8-hd",
        "speech-2.6-turbo", "speech-2.6-hd",
        "speech-02-turbo", "speech-02-hd",
        "speech-01-turbo", "speech-01-hd"
    ];

    /// <summary>Emotions accepted by the speech models ("auto" means: let the model decide).</summary>
    public static readonly string[] KnownEmotions =
    [
        "auto", "calm", "happy", "sad", "angry", "surprised", "fearful", "disgusted", "fluent", "whisper"
    ];

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(35) };
    }

    /// <summary>Synthesizes one short line. Safe to call from a worker thread.</summary>
    public static async Task<TtsSynthesisResult> SynthesizeAsync(string text, TtsSynthOptions options, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey))
            return TtsSynthesisResult.Failure("MiniMax API key is not set");

        var payload = new JsonObject
        {
            ["model"] = options.Model,
            ["text"] = text,
            ["stream"] = false,
            ["output_format"] = "hex",
            ["language_boost"] = options.LanguageBoost,
            ["text_normalization_mode"] = "basic",
            ["aigc_watermark"] = false,
            ["voice_setting"] = new JsonObject
            {
                ["voice_id"] = options.VoiceId,
                ["speed"] = Math.Clamp(options.SpeedPercent / 100.0, 0.5, 2.0),
                ["vol"] = 1.0,
                ["pitch"] = Math.Clamp(options.Pitch, -12, 12)
            },
            ["audio_setting"] = new JsonObject
            {
                ["sample_rate"] = 32000,
                ["bitrate"] = 128000,
                ["format"] = "mp3",
                ["channel"] = 1
            }
        };

        if (!string.IsNullOrWhiteSpace(options.Emotion) &&
            !string.Equals(options.Emotion, "auto", StringComparison.OrdinalIgnoreCase))
        {
            payload["voice_setting"]!["emotion"] = options.Emotion;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl(options.BaseUrl, "/v1/t2a_v2"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");

        string raw;
        try
        {
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct)
                .ConfigureAwait(false);
            raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return TtsSynthesisResult.Failure($"HTTP {(int)response.StatusCode}: {Snippet(raw)}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return TtsSynthesisResult.Failure("request cancelled");
        }
        catch (Exception ex)
        {
            return TtsSynthesisResult.Failure($"request failed: {ex.Message}");
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            if (TryReadBaseError(root, out var apiError))
                return TtsSynthesisResult.Failure(apiError);

            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
                !data.TryGetProperty("audio", out var audioNode) || audioNode.ValueKind != JsonValueKind.String)
            {
                return TtsSynthesisResult.Failure("response has no audio data");
            }

            var hex = audioNode.GetString() ?? "";
            if (hex.Length < 2)
                return TtsSynthesisResult.Failure("response carried empty audio");

            byte[] audio;
            try
            {
                audio = Convert.FromHexString(hex);
            }
            catch (FormatException)
            {
                return TtsSynthesisResult.Failure("response audio is not valid hex");
            }

            var lengthMs = 0;
            var usageChars = 0;
            if (root.TryGetProperty("extra_info", out var extra) && extra.ValueKind == JsonValueKind.Object)
            {
                if (extra.TryGetProperty("audio_length", out var len) && len.TryGetInt32(out var lenValue))
                    lengthMs = lenValue;
                if (extra.TryGetProperty("usage_characters", out var usage) && usage.TryGetInt32(out var usageValue))
                    usageChars = usageValue;
            }

            return TtsSynthesisResult.Success(audio, lengthMs, usageChars);
        }
        catch (JsonException ex)
        {
            return TtsSynthesisResult.Failure($"invalid JSON: {ex.Message}");
        }
    }

    /// <summary>Lists every voice the account may use (system + cloned + designed).</summary>
    public static async Task<(bool Ok, string Error, List<TtsVoiceInfo> Voices)> ListVoicesAsync(
        string baseUrl, string apiKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return (false, "MiniMax API key is not set", []);

        var payload = new JsonObject { ["voice_type"] = "all" };
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl(baseUrl, "/v1/get_voice"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");

        string raw;
        try
        {
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct)
                .ConfigureAwait(false);
            raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return (false, $"HTTP {(int)response.StatusCode}: {Snippet(raw)}", []);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return (false, "request cancelled", []);
        }
        catch (Exception ex)
        {
            return (false, $"request failed: {ex.Message}", []);
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (TryReadBaseError(root, out var apiError))
                return (false, apiError, []);

            var voices = new List<TtsVoiceInfo>();
            foreach (var section in new[] { "system_voice", "voice_cloning", "voice_generation" })
            {
                if (!root.TryGetProperty(section, out var array) || array.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var item in array.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                        continue;

                    var id = item.TryGetProperty("voice_id", out var idNode) ? idNode.GetString() : null;
                    if (string.IsNullOrWhiteSpace(id))
                        continue;

                    var name = item.TryGetProperty("voice_name", out var nameNode) ? nameNode.GetString() : null;
                    voices.Add(new TtsVoiceInfo(id, string.IsNullOrWhiteSpace(name) ? id : name));
                }
            }

            return (true, "", voices);
        }
        catch (JsonException ex)
        {
            return (false, $"invalid JSON: {ex.Message}", []);
        }
    }

    private static string BuildUrl(string baseUrl, string path) =>
        (string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.Trim()).TrimEnd('/') + path;

    private static bool TryReadBaseError(JsonElement root, out string error)
    {
        error = "";
        if (!root.TryGetProperty("base_resp", out var baseResp) || baseResp.ValueKind != JsonValueKind.Object)
            return false;

        if (!baseResp.TryGetProperty("status_code", out var codeNode) || !codeNode.TryGetInt32(out var code) || code == 0)
            return false;

        var message = baseResp.TryGetProperty("status_msg", out var msgNode) ? msgNode.GetString() : null;
        error = $"{code}: {message ?? "unknown error"}";
        return true;
    }

    private static string Snippet(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "(empty body)";

        var trimmed = text.Trim().Replace("\r", " ").Replace("\n", " ");
        return trimmed.Length <= 160 ? trimmed : trimmed[..160] + "…";
    }
}
