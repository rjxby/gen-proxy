using GenProxy.Api.Services.Contracts.Models;

namespace GenProxy.Api.Services.Contracts;

public interface IPromptReductionPipeline
{
    Task<PromptReductionResult> ReduceAsync(string prompt, int maxAllowedInputTokens, CancellationToken cancellationToken);
}
