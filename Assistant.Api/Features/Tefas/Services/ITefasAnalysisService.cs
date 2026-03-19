using Assistant.Api.Features.Tefas.Models;

namespace Assistant.Api.Features.Tefas.Services;

public interface ITefasAnalysisService
{
    Task<TefasAnalysisResponse> AnalyzeAsync(
        long chatId,
        string fundCode,
        CancellationToken cancellationToken);
}
