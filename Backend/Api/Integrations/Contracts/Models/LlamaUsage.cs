namespace GenProxy.Api.Integrations.Contracts.Models;

public record LlamaUsage(
    int InputTokens,
    int OutputTokens,
    int TotalTokens);
