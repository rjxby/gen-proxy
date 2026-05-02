namespace GenProxy.Api.Integrations.Contracts.Models;

public record LlamaRuntimeTrace(
    bool StructuredOutputApplied,
    bool StructuredOutputSatisfied,
    bool SpeculativeDecodingUsed);
