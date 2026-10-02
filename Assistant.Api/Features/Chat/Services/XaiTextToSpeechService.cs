using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Assistant.Api.Domain.Configurations;
using Assistant.Api.Extensions;
using Assistant.Api.Features.Chat.Models;
using Microsoft.Extensions.Options;

namespace Assistant.Api.Features.Chat.Services;

public class XaiTextToSpeechService(
    IHttpClientFactory httpClientFactory,
    IOptions<AiProvidersOptions> aiOptions,
    IOptions<EmotionOptions> emotionOptions,
    ILogger<XaiTextToSpeechService> logger
) : ITextToSpeechService
{
    private readonly XAIOptions _options = aiOptions.Value.XAI;
    private readonly EmotionOptions _emotionOptions = emotionOptions.Value;

    public async Task<byte[]> SynthesizeAsync(string text, AgentEmotionState? mood, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var sanitizedText = TtsTextSanitizer.Sanitize(text);
        if (string.IsNullOrWhiteSpace(sanitizedText))
        {
            throw new InvalidOperationException("Text contains nothing speakable after sanitization.");
        }

        var client = httpClientFactory.CreateClient(BotServiceRegistration.XAiHttpClientName);

        // Tags are added after sanitizing, so the sanitizer can't strip them.
        var (speed, wrapTag) = GetDelivery(mood);
        var request = new TtsRequest(
            wrapTag is null ? sanitizedText : $"<{wrapTag}>{sanitizedText}</{wrapTag}>",
            _options.TtsVoiceId,
            new TtsOutputFormat("mp3", 44100, 128000),
            _options.TtsLanguage,
            speed);

        using var response = await client.PostAsJsonAsync("tts", request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError(
                "XAI TTS request failed with status {StatusCode}: {ErrorBody}",
                (int)response.StatusCode,
                errorBody);
            throw new InvalidOperationException($"XAI TTS error {(int)response.StatusCode}: {errorBody}");
        }

        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    // Maps the mood quadrant around the baseline (same split as EmotionService's derived labels) to
    // xAI's documented delivery controls: the speed parameter (0.7-1.5) and wrapping speech tags.
    // There is no emotion parameter. Kept mild, so a mood never makes the audio hard to follow.
    private (double? Speed, string? WrapTag) GetDelivery(AgentEmotionState? mood)
    {
        if (mood is null)
        {
            return (null, null);
        }

        var valenceOffset = mood.Valence - _emotionOptions.BaselineValence;
        var arousalOffset = mood.Arousal - _emotionOptions.BaselineArousal;

        if (Math.Abs(valenceOffset) < EmotionService.BaselineMoodRange && Math.Abs(arousalOffset) < EmotionService.BaselineMoodRange)
        {
            return (null, null);
        }

        return (valenceOffset >= 0, arousalOffset >= 0) switch
        {
            (true, true) => (1.1, null),
            (true, false) => (0.95, "soft"),
            (false, true) => (1.05, null),
            (false, false) => (0.9, "soft")
        };
    }

    private sealed record TtsRequest(
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("voice_id")] string VoiceId,
        [property: JsonPropertyName("output_format")] TtsOutputFormat OutputFormat,
        [property: JsonPropertyName("language")] string Language,
        [property: JsonPropertyName("speed"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Speed);

    private sealed record TtsOutputFormat(
        [property: JsonPropertyName("codec")] string Codec,
        [property: JsonPropertyName("sample_rate")] int SampleRate,
        [property: JsonPropertyName("bit_rate")] int BitRate);
}
