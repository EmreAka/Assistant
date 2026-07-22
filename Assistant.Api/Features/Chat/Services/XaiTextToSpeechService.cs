using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Assistant.Api.Domain.Configurations;
using Assistant.Api.Extensions;
using Microsoft.Extensions.Options;

namespace Assistant.Api.Features.Chat.Services;

public class XaiTextToSpeechService(
    IHttpClientFactory httpClientFactory,
    IOptions<AiProvidersOptions> aiOptions,
    ILogger<XaiTextToSpeechService> logger
) : ITextToSpeechService
{
    private readonly XAIOptions _options = aiOptions.Value.XAI;

    public async Task<byte[]> SynthesizeAsync(string text, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var sanitizedText = TtsTextSanitizer.Sanitize(text);
        if (string.IsNullOrWhiteSpace(sanitizedText))
        {
            throw new InvalidOperationException("Text contains nothing speakable after sanitization.");
        }

        var client = httpClientFactory.CreateClient(BotServiceRegistration.XAiHttpClientName);

        var request = new TtsRequest(
            sanitizedText,
            _options.TtsVoiceId,
            new TtsOutputFormat("mp3", 44100, 128000),
            _options.TtsLanguage);

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

    private sealed record TtsRequest(
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("voice_id")] string VoiceId,
        [property: JsonPropertyName("output_format")] TtsOutputFormat OutputFormat,
        [property: JsonPropertyName("language")] string Language);

    private sealed record TtsOutputFormat(
        [property: JsonPropertyName("codec")] string Codec,
        [property: JsonPropertyName("sample_rate")] int SampleRate,
        [property: JsonPropertyName("bit_rate")] int BitRate);
}
