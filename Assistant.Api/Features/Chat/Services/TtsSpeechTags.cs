using System.Text.RegularExpressions;

namespace Assistant.Api.Features.Chat.Services;

// xAI TTS speech tags (https://docs.x.ai/developers/model-capabilities/audio/text-to-speech#speech-tags).
// Inline tags mark a point in the text ("[laugh]"); wrapping tags change the delivery of a phrase
// ("<soft>...</soft>").
public static partial class TtsSpeechTags
{
    public static readonly IReadOnlyList<string> Inline =
    [
        "pause", "long-pause", "hum-tune", "laugh", "chuckle", "giggle", "cry",
        "tsk", "tongue-click", "lip-smack", "breath", "inhale", "exhale", "sigh"
    ];

    public static readonly IReadOnlyList<string> Wrapping =
    [
        "soft", "whisper", "loud", "build-intensity", "decrease-intensity",
        "higher-pitch", "lower-pitch", "slow", "fast", "sing-song", "singing", "emphasis"
    ];

    /// <summary>
    /// Checks a model-tagged script against the text it was made from. Valid means: only known tags,
    /// every wrapping tag closed in order, and with the tags removed the words and punctuation are
    /// exactly the original (whitespace aside). The model may only add delivery, never change what
    /// is said.
    /// </summary>
    public static bool IsValidScript(string original, string tagged)
    {
        var openTags = new Stack<string>();
        foreach (Match match in TagPattern().Matches(tagged))
        {
            if (match.Groups["inline"].Success)
            {
                if (!Inline.Contains(match.Groups["inline"].Value))
                {
                    return false;
                }

                continue;
            }

            var name = match.Groups["name"].Value;
            if (!Wrapping.Contains(name))
            {
                return false;
            }

            if (!match.Groups["close"].Success)
            {
                openTags.Push(name);
            }
            else if (!openTags.TryPop(out var open) || open != name)
            {
                return false;
            }
        }

        // Whitespace is ignored entirely: removing "[laugh]" from "Wow [laugh]." leaves "Wow .".
        return openTags.Count == 0
            && WhitespacePattern().Replace(TagPattern().Replace(tagged, ""), "") == WhitespacePattern().Replace(original, "");
    }

    // Matches any bracket or angle tag shaped like a speech tag, known or not, so unknown ones fail
    // validation instead of being read aloud.
    [GeneratedRegex(@"\[(?<inline>[a-z-]+)\]|<(?<close>/)?(?<name>[a-z-]+)>")]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();
}
