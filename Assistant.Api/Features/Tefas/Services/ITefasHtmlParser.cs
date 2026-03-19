using Assistant.Api.Features.Tefas.Models;

namespace Assistant.Api.Features.Tefas.Services;

public interface ITefasHtmlParser
{
    TefasParseResult Parse(
        string html,
        string requestedCode,
        string sourceUrl);
}
