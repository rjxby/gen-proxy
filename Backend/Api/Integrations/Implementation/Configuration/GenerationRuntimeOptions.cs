namespace GenProxy.Api.Integrations.Implementation.Configuration;

public class GenerationRuntimeOptions
{
    public const string SectionName = "GenerationRuntime";

    public string Address { get; set; } = "https://localhost:50051";

    public string? ApiKey { get; set; }
}
