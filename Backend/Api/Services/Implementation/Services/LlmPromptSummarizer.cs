using GenProxy.Api.Integrations.Contracts;
using GenProxy.Api.Integrations.Contracts.Configuration;
using GenProxy.Api.Services.Contracts;
using GenProxy.Api.Services.Contracts.Models;
using GenProxy.Api.Services.Implementation.Configuration;
using Microsoft.Extensions.Options;

namespace GenProxy.Api.Services.Implementation.Services;

public sealed class LlmPromptSummarizer(
    IPromptReducerRuntimeClient runtimeClient,
    IOptions<PromptReducerRuntimeOptions> runtimeOptions,
    IOptions<PromptReductionOptions> options) : IPromptReducer
{
    public int Order => 100;

    private readonly IPromptReducerRuntimeClient _runtimeClient = runtimeClient;
    private readonly PromptReducerRuntimeOptions _runtimeOptions = runtimeOptions.Value;
    private readonly PromptReductionOptions _options = options.Value;

    public async Task<PromptReductionResult> ReduceAsync(string prompt, int maxAllowedInputTokens, CancellationToken cancellationToken)
    {
        if (!_runtimeOptions.Enabled)
        {
            return new PromptReductionResult(prompt, false, PromptReductionStrategy.None);
        }

        var reductionPrompt = _options.SummarizationPromptTemplate
            .Replace("{{max_tokens}}", maxAllowedInputTokens.ToString(), StringComparison.Ordinal)
            .Replace("{{prompt}}", prompt, StringComparison.Ordinal);

        try
        {
            var response = await _runtimeClient.GenerateAsync(
                $"reduce_{Guid.NewGuid():N}",
                reductionPrompt,
                options: null,
                cancellationToken);

            var reducedPrompt = response.Content.Trim();
            var wasReduced = !string.Equals(prompt, reducedPrompt, StringComparison.Ordinal);

            return new PromptReductionResult(reducedPrompt, wasReduced, PromptReductionStrategy.LlmSummarizer);
        }
        catch (LlamaRuntimeCallException)
        {
            return new PromptReductionResult(prompt, false, PromptReductionStrategy.None);
        }
        catch (LlamaRuntimePromptBudgetExceededException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new PromptReductionFailedException("The prompt reducer runtime could not produce a reduced prompt.", exception);
        }
    }
}
