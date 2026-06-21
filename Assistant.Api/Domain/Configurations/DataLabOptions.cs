namespace Assistant.Api.Domain.Configurations;

public class DataLabOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string ApiUrl { get; set; } = "https://www.datalab.to/api/v1";
    public string PipelineId { get; set; } = "pl_pUrNTITLG60g";
    public int ResultStepIndex { get; set; }
    public int MaxPollAttempts { get; set; } = 300;
    public int PollIntervalSeconds { get; set; } = 2;
}
