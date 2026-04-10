namespace GenProxy.Api.Integrations.Contracts.Models;

public record TokenEstimation(
    int TokenCount,
    int ContextSize,
    int ReservedOutputTokens,
    int MaxAllowedInputTokens,
    bool Fits);
