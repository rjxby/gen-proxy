namespace GenProxy.Api.Integrations.Contracts.Models;

public record LlamaGenerationResult(
    string RequestId,
    string Model,
    string Content,
    LlamaUsage? Usage,
    LlamaRuntimeTrace? RuntimeTrace);
