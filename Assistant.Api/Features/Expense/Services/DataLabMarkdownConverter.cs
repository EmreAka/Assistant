using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Assistant.Api.Domain.Configurations;
using Assistant.Api.Extensions;
using Microsoft.Extensions.Options;

namespace Assistant.Api.Features.Expense.Services;

public class DataLabMarkdownConverter(
    IHttpClientFactory httpClientFactory,
    IOptions<DataLabOptions> dataLabOptions,
    ILogger<DataLabMarkdownConverter> logger
) : IMarkdownConverter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly DataLabOptions _options = dataLabOptions.Value;

    public async Task<string> ConvertToMarkdownAsync(Stream documentStream, CancellationToken cancellationToken)
    {
        ValidateOptions(_options);

        if (documentStream.CanSeek)
        {
            documentStream.Position = 0;
        }

        using var httpClient = httpClientFactory.CreateClient(BotServiceRegistration.DataLabHttpClientName);
        var execution = await RunPipelineAsync(httpClient, documentStream, cancellationToken);
        var completedExecution = await PollPipelineExecutionAsync(httpClient, execution, cancellationToken);
        var markdown = await GetStepMarkdownAsync(httpClient, completedExecution.ExecutionId!, cancellationToken);

        if (string.IsNullOrWhiteSpace(markdown))
        {
            throw new InvalidOperationException("DataLab pipeline result did not contain markdown.");
        }

        logger.LogInformation(
            "DataLab markdown conversion completed. ExecutionId={ExecutionId}, MarkdownLength={MarkdownLength}",
            completedExecution.ExecutionId,
            markdown.Length);

        return markdown;
    }

    private async Task<PipelineExecutionResponse> RunPipelineAsync(
        HttpClient httpClient,
        Stream documentStream,
        CancellationToken cancellationToken)
    {
        using var form = new MultipartFormDataContent();
        using var fileContent = new StreamContent(documentStream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");

        form.Add(fileContent, "file", "statement.pdf");
        form.Add(new StringContent("markdown"), "output_format");

        var pipelineId = Uri.EscapeDataString(_options.PipelineId);
        using var response = await httpClient.PostAsync($"pipelines/{pipelineId}/run", form, cancellationToken);
        await EnsureSuccessStatusCodeAsync(response, "DataLab pipeline run", cancellationToken);

        var execution = await DeserializeJsonAsync<PipelineExecutionResponse>(
            response,
            "DataLab pipeline run",
            cancellationToken);

        if (string.IsNullOrWhiteSpace(execution.ExecutionId))
        {
            throw new InvalidOperationException("DataLab pipeline run did not return an execution ID.");
        }

        logger.LogInformation(
            "DataLab pipeline execution started. ExecutionId={ExecutionId}, Status={Status}",
            execution.ExecutionId,
            execution.Status);

        return execution;
    }

    private async Task<PipelineExecutionResponse> PollPipelineExecutionAsync(
        HttpClient httpClient,
        PipelineExecutionResponse execution,
        CancellationToken cancellationToken)
    {
        if (IsTerminalStatus(execution.Status))
        {
            EnsureSuccessfulExecution(execution);
            return execution;
        }

        var maxPollAttempts = Math.Max(1, _options.MaxPollAttempts);
        var pollDelay = TimeSpan.FromSeconds(Math.Max(1, _options.PollIntervalSeconds));
        var executionId = Uri.EscapeDataString(execution.ExecutionId!);

        for (var attempt = 1; attempt <= maxPollAttempts; attempt++)
        {
            await Task.Delay(pollDelay, cancellationToken);

            using var response = await httpClient.GetAsync($"pipelines/executions/{executionId}", cancellationToken);
            await EnsureSuccessStatusCodeAsync(response, "DataLab pipeline execution polling", cancellationToken);

            execution = await DeserializeJsonAsync<PipelineExecutionResponse>(
                response,
                "DataLab pipeline execution polling",
                cancellationToken);

            logger.LogInformation(
                "DataLab pipeline execution poll. ExecutionId={ExecutionId}, Status={Status}, Attempt={Attempt}/{MaxPollAttempts}",
                execution.ExecutionId,
                execution.Status,
                attempt,
                maxPollAttempts);

            if (IsTerminalStatus(execution.Status))
            {
                EnsureSuccessfulExecution(execution);
                return execution;
            }
        }

        throw new TimeoutException(
            $"DataLab pipeline execution did not complete after {maxPollAttempts} polling attempts.");
    }

    private async Task<string> GetStepMarkdownAsync(
        HttpClient httpClient,
        string executionId,
        CancellationToken cancellationToken)
    {
        var escapedExecutionId = Uri.EscapeDataString(executionId);
        using var response = await httpClient.GetAsync(
            $"pipelines/executions/{escapedExecutionId}/steps/{_options.ResultStepIndex}/result",
            cancellationToken);
        await EnsureSuccessStatusCodeAsync(response, "DataLab pipeline step result", cancellationToken);

        var rawResult = await response.Content.ReadAsStringAsync(cancellationToken);
        return ExtractMarkdown(rawResult);
    }

    private static string ExtractMarkdown(string rawResult)
    {
        if (string.IsNullOrWhiteSpace(rawResult))
        {
            return string.Empty;
        }

        using var document = JsonDocument.Parse(rawResult);
        var root = document.RootElement;

        if (root.ValueKind == JsonValueKind.String)
        {
            return root.GetString() ?? string.Empty;
        }

        if (root.TryGetProperty("markdown", out var markdownElement) &&
            markdownElement.ValueKind == JsonValueKind.String)
        {
            return markdownElement.GetString() ?? string.Empty;
        }

        if (root.TryGetProperty("result", out var resultElement) &&
            resultElement.ValueKind == JsonValueKind.Object &&
            resultElement.TryGetProperty("markdown", out var nestedMarkdownElement) &&
            nestedMarkdownElement.ValueKind == JsonValueKind.String)
        {
            return nestedMarkdownElement.GetString() ?? string.Empty;
        }

        throw new InvalidOperationException("DataLab pipeline step result did not include a markdown field.");
    }

    private static async Task<T> DeserializeJsonAsync<T>(
        HttpResponseMessage response,
        string operationName,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var result = await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken);

        if (result is null)
        {
            throw new InvalidOperationException($"{operationName} returned an empty JSON body.");
        }

        return result;
    }

    private static async Task EnsureSuccessStatusCodeAsync(
        HttpResponseMessage response,
        string operationName,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException(
            $"{operationName} failed with HTTP {(int)response.StatusCode} {response.ReasonPhrase}. Response={TruncateForLog(responseBody)}");
    }

    private static void EnsureSuccessfulExecution(PipelineExecutionResponse execution)
    {
        var status = NormalizeStatus(execution.Status);
        if (status == "completed")
        {
            return;
        }

        var stepErrors = execution.Steps?
            .Where(step => !string.IsNullOrWhiteSpace(step.ErrorMessage))
            .Select(step => $"step {step.StepIndex} ({step.StepType ?? "unknown"}): {step.ErrorMessage}")
            .ToList() ?? [];

        var errorDetails = stepErrors.Count > 0
            ? string.Join("; ", stepErrors)
            : "No step error details were returned.";

        throw new InvalidOperationException(
            $"DataLab pipeline execution ended with status '{execution.Status}'. {errorDetails}");
    }

    private static bool IsTerminalStatus(string? status)
    {
        return NormalizeStatus(status) is "completed" or "completed_with_errors" or "failed";
    }

    private static string NormalizeStatus(string? status)
    {
        return status?.Trim().ToLowerInvariant() ?? string.Empty;
    }

    private static void ValidateOptions(DataLabOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new InvalidOperationException("DataLab:ApiKey is not configured.");
        }

        if (string.IsNullOrWhiteSpace(options.ApiUrl))
        {
            throw new InvalidOperationException("DataLab:ApiUrl is not configured.");
        }

        if (string.IsNullOrWhiteSpace(options.PipelineId))
        {
            throw new InvalidOperationException("DataLab:PipelineId is not configured.");
        }

        if (options.ResultStepIndex < 0)
        {
            throw new InvalidOperationException("DataLab:ResultStepIndex cannot be negative.");
        }
    }

    private static string TruncateForLog(string? value, int maxLength = 2000)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

        return normalized.Length <= maxLength
            ? normalized
            : normalized[..maxLength] + "\n...[truncated]";
    }
}

internal sealed record PipelineExecutionResponse(
    [property: JsonPropertyName("execution_id")] string? ExecutionId,
    [property: JsonPropertyName("pipeline_id")] string? PipelineId,
    [property: JsonPropertyName("pipeline_version")] int? PipelineVersion,
    string? Status,
    List<PipelineExecutionStepResponse>? Steps
);

internal sealed record PipelineExecutionStepResponse(
    [property: JsonPropertyName("step_index")] int StepIndex,
    [property: JsonPropertyName("step_type")] string? StepType,
    string? Status,
    [property: JsonPropertyName("lookup_key")] string? LookupKey,
    [property: JsonPropertyName("result_url")] string? ResultUrl,
    [property: JsonPropertyName("error_message")] string? ErrorMessage
);
