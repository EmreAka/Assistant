using Assistant.Api.Domain.Configurations;
using Assistant.Api.Services.Abstracts;
using Google.GenAI;
using Google.GenAI.Types;
using Microsoft.Extensions.Options;
using Pgvector;

namespace Assistant.Api.Services.Concretes;

public class EmbeddingService(
    IOptions<AiOptions> aiOptions,
    ILogger<EmbeddingService> logger
) : IEmbeddingService
{
    private readonly AiOptions _aiOptions = aiOptions.Value;

    public Task<Vector?> GenerateDocumentEmbeddingAsync(string text, string? title, CancellationToken cancellationToken)
    {
        return GenerateEmbeddingAsync(text, "RETRIEVAL_DOCUMENT", title, cancellationToken);
    }

    public Task<Vector?> GenerateQueryEmbeddingAsync(string text, CancellationToken cancellationToken)
    {
        return GenerateEmbeddingAsync(text, "RETRIEVAL_QUERY", null, cancellationToken);
    }

    private async Task<Vector?> GenerateEmbeddingAsync(
        string text,
        string taskType,
        string? title,
        CancellationToken cancellationToken)
    {
        var normalizedText = MemoryNormalization.NormalizeContent(text);
        if (string.IsNullOrWhiteSpace(normalizedText))
        {
            return null;
        }

        try
        {
            var client = new Client(apiKey: _aiOptions.GoogleApiKey);
            var response = await client.Models.EmbedContentAsync(
                model: _aiOptions.EmbeddingModel,
                contents: normalizedText,
                config: new EmbedContentConfig
                {
                    TaskType = taskType,
                    Title = taskType == "RETRIEVAL_DOCUMENT" ? title : null,
                    OutputDimensionality = _aiOptions.EmbeddingDimensions
                },
                cancellationToken: cancellationToken);

            var values = response.Embeddings?.FirstOrDefault()?.Values;
            if (values is null || values.Count == 0)
            {
                logger.LogWarning("Gemini embedding response was empty for task type {TaskType}.", taskType);
                return null;
            }

            return NormalizeVector(values.Select(value => (float)value).ToArray());
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to generate Gemini embedding for task type {TaskType}.", taskType);
            return null;
        }
    }

    private static Vector NormalizeVector(float[] values)
    {
        double magnitude = 0;
        foreach (var value in values)
        {
            magnitude += value * value;
        }

        if (magnitude <= double.Epsilon)
        {
            return new Vector(values);
        }

        var scale = 1d / Math.Sqrt(magnitude);
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = (float)(values[i] * scale);
        }

        return new Vector(values);
    }
}
