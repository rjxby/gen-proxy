using GenProxy.Api.Services.Contracts.Models;
using GenProxy.Api.Services.Contracts;

namespace GenProxy.Api.Services.Implementation.Services;

public sealed class PromptReductionPipeline(IEnumerable<IPromptReducer> reducers) : IPromptReductionPipeline
{
    private readonly IReadOnlyList<IPromptReducer> _reducers = reducers
        .OrderBy(reducer => reducer.Order)
        .ToList();

    public async Task<PromptReductionResult> ReduceAsync(string prompt, int maxAllowedInputTokens, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var currentPrompt = prompt;

        foreach (var reducer in _reducers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await reducer.ReduceAsync(currentPrompt, maxAllowedInputTokens, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            currentPrompt = result.Prompt;

            if (result.WasReduced)
            {
                return result;
            }
        }

        return new PromptReductionResult(currentPrompt, false, PromptReductionStrategy.None);
    }
}
