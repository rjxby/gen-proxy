using GenProxy.Api.Services.Contracts;

namespace GenProxy.Api.Services.Contracts.Models;

public record PromptReductionResult(string Prompt, bool WasReduced, PromptReductionStrategy Strategy);
