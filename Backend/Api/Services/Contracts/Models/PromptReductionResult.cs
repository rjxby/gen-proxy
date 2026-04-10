namespace GenProxy.Api.Services.Contracts.Models;

public record PromptReductionResult(string Prompt, bool WasReduced, string Strategy);
