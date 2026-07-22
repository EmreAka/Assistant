namespace Assistant.Api.Features.Chat.Services;

public interface ITextToSpeechService
{
    Task<byte[]> SynthesizeAsync(string text, CancellationToken cancellationToken);
}
