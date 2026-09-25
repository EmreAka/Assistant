using Pgvector;

namespace Assistant.Api.Features.Chat.Services;

public interface IChatTurnEmbeddingService
{
    Task<Vector> EmbedDocumentAsync(string text, CancellationToken cancellationToken);
}
