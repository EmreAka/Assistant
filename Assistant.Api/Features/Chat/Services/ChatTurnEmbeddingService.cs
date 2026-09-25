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
    // Stored turns use the document prefix and searches use the query prefix (asymmetric retrieval).
    private const string DocumentPrefix = "title: none | text: ";
    private const string QueryPrefix = "task: search result | query: ";

    private readonly EmbeddingOptions _options = options.Value;

    public Task<Vector> EmbedDocumentAsync(string text, CancellationToken cancellationToken)
    {
        return EmbedAsync(DocumentPrefix, text, cancellationToken);
    }

    public Task<Vector> EmbedQueryAsync(string text, CancellationToken cancellationToken)
    {
        return EmbedAsync(QueryPrefix, text, cancellationToken);
    }

    private async Task<Vector> EmbedAsync(string prefix, string text, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        // Always exactly one input per request: OpenRouter routes multi-input requests to a
        // non-ZDR batch endpoint, which our guardrail rejects.
        var embeddings = await embeddingGenerator.GenerateAsync(
            [prefix + text],
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
