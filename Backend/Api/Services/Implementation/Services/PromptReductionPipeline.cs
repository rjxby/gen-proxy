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
        var currentPrompt = prompt;
        var wasReduced = false;
        var strategy = PromptReductionStrategy.None;

        foreach (var reducer in _reducers)
        {
            var result = await reducer.ReduceAsync(currentPrompt, maxAllowedInputTokens, cancellationToken);
            currentPrompt = result.Prompt;

            if (!result.WasReduced)
            {
                continue;
            }

            wasReduced = true;
            strategy = result.Strategy;
            break;
        }

        return new PromptReductionResult(currentPrompt, wasReduced, strategy);
    }
}
