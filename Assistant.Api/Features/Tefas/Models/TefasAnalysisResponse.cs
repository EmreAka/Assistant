namespace Assistant.Api.Features.Tefas.Models;

public sealed record TefasAnalysisResponse(
    bool Found,
    string UserMessage,
    TefasFundSnapshot? Fund = null);
