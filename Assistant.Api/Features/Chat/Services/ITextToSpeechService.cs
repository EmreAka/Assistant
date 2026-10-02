using Assistant.Api.Features.Chat.Models;

namespace Assistant.Api.Features.Chat.Services;

public interface ITextToSpeechService
{
    /// <summary>Synthesizes the text. A mood shifts the delivery slightly; null speaks it neutrally.</summary>
    Task<byte[]> SynthesizeAsync(string text, AgentEmotionState? mood, CancellationToken cancellationToken);
}
