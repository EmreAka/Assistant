using Pgvector;

namespace Assistant.Api.Services.Abstracts;

public interface IEmbeddingService
{
    Task<Vector?> GenerateDocumentEmbeddingAsync(string text, string? title, CancellationToken cancellationToken);
    Task<Vector?> GenerateQueryEmbeddingAsync(string text, CancellationToken cancellationToken);
}
