namespace Assistant.Api.Domain.Configurations;

public class CodeSandboxMcpOptions
{
    public string Command { get; set; } = "code-sandbox-mcp";
    public string[] Arguments { get; set; } = [];
    public string ContainerImage { get; set; } = "philschmi/code-sandbox-python:latest";
    public string ContainerLanguage { get; set; } = "python";
}
