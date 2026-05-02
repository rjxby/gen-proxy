using GenProxy.Api.Services.Contracts;

namespace GenProxy.Api.Services.Contracts.Models;

public record GeneratedResponse(
    string ResponseId,
    string Model,
    string OutputText,
    string FinalPrompt,
    bool WasReduced,
    PromptReductionStrategy ReductionStrategy,
    int InputTokens,
    int? OutputTokens,
    int? TotalTokens,
    int ContextSize,
    int ReservedOutputTokens,
    int MaxAllowedInputTokens,
    DateTimeOffset CreatedAt);
