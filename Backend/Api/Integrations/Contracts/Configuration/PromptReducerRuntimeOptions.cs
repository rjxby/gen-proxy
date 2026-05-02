namespace GenProxy.Api.Integrations.Contracts.Configuration;

public class PromptReducerRuntimeOptions
{
    public const string SectionName = "PromptReducerRuntime";

    public bool Enabled { get; set; } = true;

    public string Address { get; set; } = "https://localhost:50052";

    public string? ApiKey { get; set; }
}
