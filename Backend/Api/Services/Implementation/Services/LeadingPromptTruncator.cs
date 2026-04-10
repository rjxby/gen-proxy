using GenProxy.Api.Integrations.Contracts;
using GenProxy.Api.Services.Contracts;
using GenProxy.Api.Services.Contracts.Models;

namespace GenProxy.Api.Services.Implementation.Services;

public sealed class LeadingPromptTruncator(IGenerationRuntimeClient generationRuntimeClient) : IPromptReducer
{
    public int Order => 200;

    private readonly IGenerationRuntimeClient _generationRuntimeClient = generationRuntimeClient;

    public async Task<PromptReductionResult> ReduceAsync(string prompt, int maxAllowedInputTokens, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(prompt))
        {
            return new PromptReductionResult(prompt, false, "none");
        }

        var currentPrompt = prompt;

        while (currentPrompt.Length > 0)
        {
            var estimation = await _generationRuntimeClient.EstimateTokensAsync(currentPrompt, cancellationToken);
            if (estimation.Fits)
            {
                break;
            }

            var excessTokens = Math.Max(1, estimation.TokenCount - maxAllowedInputTokens);
            var trimAmount = Math.Max(
                1,
                (int)Math.Ceiling((double)currentPrompt.Length * excessTokens / Math.Max(estimation.TokenCount, 1)));

            currentPrompt = trimAmount >= currentPrompt.Length
                ? string.Empty
                : currentPrompt[trimAmount..];
        }

        var wasReduced = !string.Equals(prompt, currentPrompt, StringComparison.Ordinal);
        return new PromptReductionResult(currentPrompt, wasReduced, wasReduced ? "leading_truncation" : "none");
    }
}
