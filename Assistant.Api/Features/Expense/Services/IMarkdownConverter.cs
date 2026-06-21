namespace Assistant.Api.Features.Expense.Services;

public interface IMarkdownConverter
{
    Task<string> ConvertToMarkdownAsync(Stream documentStream, CancellationToken cancellationToken);
}
