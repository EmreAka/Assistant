using Assistant.Api.Domain.Configurations;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Pgvector;

namespace Assistant.Api.Features.Chat.Services;

public class ChatTurnEmbeddingService(
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    IOptions<EmbeddingOptions> options
) : IChatTurnEmbeddingService
{
    // Gemini Embedding 2 has no task_type parameter; the retrieval task is expressed as a text prefix.
    private const string DocumentPrefix = "title: none | text: ";

    private readonly EmbeddingOptions _options = options.Value;

    public async Task<Vector> EmbedDocumentAsync(string text, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        // Always exactly one input per request: OpenRouter routes multi-input requests to a
        // non-ZDR batch endpoint, which our guardrail rejects.
        var embeddings = await embeddingGenerator.GenerateAsync(
            [DocumentPrefix + text],
            new EmbeddingGenerationOptions { Dimensions = _options.Dimensions },
            cancellationToken);

        var vector = embeddings.Single().Vector;
        if (vector.Length != _options.Dimensions)
        {
            throw new InvalidOperationException(
                $"Expected an embedding with {_options.Dimensions} dimensions but got {vector.Length}.");
        }

        return new Vector(vector);
    }
}
