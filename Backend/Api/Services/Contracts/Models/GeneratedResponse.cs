namespace GenProxy.Api.Services.Contracts.Models;

public record GeneratedResponse(
    string ResponseId,
    string Model,
    string OutputText,
    string FinalPrompt,
    bool WasReduced,
    string ReductionStrategy,
    int InputTokens,
    int ContextSize,
    int ReservedOutputTokens,
    int MaxAllowedInputTokens,
    DateTimeOffset CreatedAt);
