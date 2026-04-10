namespace GenProxy.Api.Integrations.Implementation.Configuration;

public class PromptReducerRuntimeOptions
{
    public const string SectionName = "PromptReducerRuntime";

    public string Address { get; set; } = "https://localhost:50052";

    public string? ApiKey { get; set; }
}
