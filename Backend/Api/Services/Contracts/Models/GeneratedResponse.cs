using GenProxy.Api.Services.Contracts;

namespace GenProxy.Api.Services.Contracts.Models;

public record GeneratedResponse(
    string ResponseId,
    string Model,
    string OutputText,
    bool WasReduced,
    PromptReductionStrategy ReductionStrategy,
    int InputTokens,
    int? OutputTokens,
    int? TotalTokens,
    int MaxAllowedInputTokens,
    DateTimeOffset CreatedAt);
