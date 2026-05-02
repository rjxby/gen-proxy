namespace GenProxy.Api.Integrations.Contracts.Models;

public record LlamaCapabilities(
    string ModelId,
    int ContextSize,
    bool SupportsStructuredOutput,
    bool SupportsJsonObjectOutput,
    bool SupportsSpeculativeDecoding,
    string TokenizerFamily);
