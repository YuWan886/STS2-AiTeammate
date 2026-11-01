using System.Text;
using System.Text.RegularExpressions;

namespace AiTeammate.AiTeammateCode.Voice;

/// <summary>
/// Turns a display line into something worth reading aloud: markdown/BBCode/emoji/URLs and stage
/// directions are stripped, laughter/sigh cues become MiniMax interjection tags, and overly long
/// lines are split into sentence-sized requests (billing is per character, so short is cheap).
/// 台词清洗与切句。
/// </summary>
public static class TtsTextNormalizer
{
    /// <summary>MiniMax pause marker; a dramatic ellipsis reads much better as a short silence.</summary>
    private const string PauseMark = "<#0.3#>";

    private static readonly Regex CodeFence = new("```[\\s\\S]*?```", RegexOptions.Compiled);
    private static readonly Regex WebAddress = new(@"https?://\S+|www\.\S+", RegexOptions.Compiled);
    private static readonly Regex BbcodeTag = new(@"\[/?[a-zA-Z][^\]\r\n]{0,40}\]", RegexOptions.Compiled);
    private static readonly Regex StageAction = new(@"\*[^*\r\n]{1,40}\*", RegexOptions.Compiled);
    private static readonly Regex Parenthetical = new(@"[（(]([^）)\r\n]{1,12})[）)]", RegexOptions.Compiled);
    private static readonly Regex MarkdownMarks = new(@"[*_~`#>|\r\n\t]+", RegexOptions.Compiled);
    private static readonly Regex Ellipsis = new(@"(……|…|\.{3,})", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"[ \u00A0]{2,}", RegexOptions.Compiled);
    private static readonly Regex SpaceBeforePunctuation = new(@"[ \u00A0]+([，。！？；：、,.!?;:])", RegexOptions.Compiled);
    private static readonly Regex PauseMarker = new(@"<#[0-9.]+#>", RegexOptions.Compiled);
    private static readonly Regex AsciiInterjection = new(@"[（(][a-zA-Z\- ]{2,20}[）)]", RegexOptions.Compiled);

    /// <summary>Parenthetical stage directions that MiniMax 2.8 can actually voice as interjections.</summary>
    private static readonly Dictionary<string, string> CueTags = new(StringComparer.OrdinalIgnoreCase)
    {
        ["笑"] = "(laughs)",
        ["哈哈"] = "(laughs)",
        ["笑死"] = "(laughs)",
        ["笑出声"] = "(laughs)",
        ["轻笑"] = "(chuckle)",
        ["苦笑"] = "(chuckle)",
        ["叹气"] = "(sighs)",
        ["叹口气"] = "(sighs)",
        [" sigh"] = "(sighs)",
        ["sigh"] = "(sighs)",
        ["sighs"] = "(sighs)",
        ["咳嗽"] = "(coughs)",
        ["清嗓"] = "(clear-throat)",
        ["清嗓子"] = "(clear-throat)",
        ["喘气"] = "(pant)",
        ["吸气"] = "(inhale)",
        ["呼气"] = "(exhale)",
        ["哼唱"] = "(humming)",
        ["嗯"] = "(emm)"
    };

    /// <summary>Display text → speakable text. Returns an empty string when nothing is left to read.</summary>
    /// <param name="raw">The chat line as shown in the panel.</param>
    /// <param name="allowInterjections">
    /// MiniMax reads <c>(laughs)</c>-style tags only on the speech-2.8 models; on older models the
    /// parenthesis content is simply dropped instead of being left for the voice to spell out.
    /// </param>
    public static string Clean(string? raw, bool allowInterjections = true)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "";

        var text = raw;

        text = CodeFence.Replace(text, " ");
        text = WebAddress.Replace(text, " ");
        text = BbcodeTag.Replace(text, " ");
        text = StageAction.Replace(text, " ");
        text = Parenthetical.Replace(text, match =>
            allowInterjections && CueTags.TryGetValue(match.Groups[1].Value.Trim(), out var tag)
                ? " " + tag + " "
                : " ");
        text = MarkdownMarks.Replace(text, " ");
        text = Ellipsis.Replace(text, " " + PauseMark + " ");
        text = StripEmoji(text);
        text = text.Replace("【", "").Replace("】", " ").Replace("「", "").Replace("」", " ");
        text = Spaces.Replace(text, " ");
        text = SpaceBeforePunctuation.Replace(text, "$1");

        // A pause marker at the very start/end adds dead air; strip it.
        text = text.Trim();
        while (text.StartsWith(PauseMark, StringComparison.Ordinal))
            text = text[PauseMark.Length..].TrimStart();
        while (text.EndsWith(PauseMark, StringComparison.Ordinal))
            text = text[..^PauseMark.Length].TrimEnd();

        text = text.Trim();

        // Nothing but punctuation/whitespace left (for example "。。。") — not worth a request.
        var hasSpeakable = false;
        foreach (var ch in text)
        {
            if (!char.IsLetterOrDigit(ch))
                continue;
            hasSpeakable = true;
            break;
        }

        return hasSpeakable ? text : "";
    }

    /// <summary>
    /// Splits cleaned text into speech requests of at most <paramref name="maxChars"/> characters,
    /// greedily merging whole sentences so a normal reply normally costs exactly one request.
    /// </summary>
    public static IReadOnlyList<string> Split(string text, int maxChars)
    {
        maxChars = Math.Clamp(maxChars, 20, 300);
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
            return result;

        var sentences = new List<string>();
        var sentence = new StringBuilder();
        foreach (var ch in text)
        {
            sentence.Append(ch);
            if (ch is not ('。' or '！' or '？' or '!' or '?' or '；' or ';' or '\n'))
                continue;

            AppendSentence(sentences, sentence.ToString(), maxChars);
            sentence.Clear();
        }

        if (sentence.Length > 0)
            AppendSentence(sentences, sentence.ToString(), maxChars);

        var current = new StringBuilder();
        foreach (var part in sentences)
        {
            if (current.Length > 0 && current.Length + part.Length > maxChars)
            {
                result.Add(current.ToString());
                current.Clear();
            }

            // Latin sentences need the space the display text dropped between them; CJK does not.
            if (current.Length > 0 && current[^1] < 128 && part.Length > 0 && part[0] < 128)
                current.Append(' ');

            current.Append(part);
        }

        if (current.Length > 0)
            result.Add(current.ToString());

        return result;
    }

    /// <summary>True when the configured model understands <c>(laughs)</c>-style interjection tags.</summary>
    public static bool SupportsInterjections(string? model) =>
        !string.IsNullOrWhiteSpace(model) && model.Contains("2.8", StringComparison.OrdinalIgnoreCase);

    /// <summary>Adds one sentence, hard-splitting it when it alone exceeds the request limit.</summary>
    private static void AppendSentence(List<string> sentences, string chunk, int maxChars)
    {
        chunk = chunk.Trim();
        if (chunk.Length == 0)
            return;

        while (chunk.Length > maxChars)
        {
            var cut = chunk.LastIndexOfAny(['，', '、', ',', ' '], maxChars - 1);
            if (cut < maxChars / 2)
                cut = maxChars - 1;

            var head = chunk[..(cut + 1)].Trim();
            if (head.Length > 0)
                sentences.Add(head);

            chunk = chunk[(cut + 1)..].TrimStart();
        }

        if (chunk.Length > 0)
            sentences.Add(chunk);
    }

    /// <summary>MiniMax <c>language_boost</c> guess: pure Chinese / pure Latin / mixed.</summary>
    public static string DetectLanguageBoost(string text)
    {
        // Interjection tags and pause markers are Latin-looking but say nothing about the language.
        var probe = PauseMarker.Replace(text, " ");
        probe = AsciiInterjection.Replace(probe, " ");

        var hasCjk = false;
        var hasLatin = false;
        foreach (var ch in probe)
        {
            if (IsCjk(ch))
                hasCjk = true;
            else if (ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
                hasLatin = true;

            if (hasCjk && hasLatin)
                return "auto";
        }

        if (hasCjk)
            return "Chinese";

        return hasLatin ? "English" : "auto";
    }

    /// <summary>MiniMax bills 1 per Latin/punctuation character and 2 per Chinese character.</summary>
    public static int EstimateBilledChars(string text)
    {
        var total = 0;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
                continue;
            total += IsCjk(ch) ? 2 : 1;
        }

        return total;
    }

    private static bool IsCjk(char ch) =>
        ch is >= '\u4E00' and <= '\u9FFF' or >= '\u3400' and <= '\u4DBF' or >= '\uF900' and <= '\uFAFF';

    private static string StripEmoji(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            // Surrogates (all non-BMP emoji), variation selectors, joiners, keycaps, and the
            // common symbol blocks the game font renders as pictures.
            if (char.IsSurrogate(ch) || ch == '\u200D' || ch == '\uFE0F' || ch == '\u20E3')
                continue;
            if (ch is >= '\u2600' and <= '\u27BF')
                continue;
            if (ch is >= '\uE000' and <= '\uF8FF')
                continue;

            sb.Append(ch);
        }

        return sb.ToString();
    }
}
