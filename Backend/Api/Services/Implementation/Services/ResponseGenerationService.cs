using GenProxy.Api.Integrations.Contracts;
using GenProxy.Api.Services.Contracts;
using GenProxy.Api.Services.Contracts.Models;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace GenProxy.Api.Services.Implementation.Services;

public class ResponseGenerationService(
    IGenerationRuntimeClient generationRuntimeClient,
    IPromptReductionPipeline promptReductionPipeline,
    ILogger<ResponseGenerationService> logger) : IResponseGenerationService
{
    private readonly IGenerationRuntimeClient _generationRuntimeClient = generationRuntimeClient;
    private readonly IPromptReductionPipeline _promptReductionPipeline = promptReductionPipeline;
    private readonly ILogger<ResponseGenerationService> _logger = logger;

    public async Task<GeneratedResponse> GenerateAsync(ResponseCreateCommand command, CancellationToken cancellationToken)
    {
        var requestId = $"resp_{Guid.NewGuid():N}";
        var effectivePrompt = command.Input;
        var requestStopwatch = Stopwatch.StartNew();

        GenProxyMetrics.RequestsStarted.Add(1, KeyValuePair.Create<string, object?>("model", command.Model));

        try
        {
            _logger.LogInformation("Starting response generation. RequestId={RequestId} Model={Model}", requestId, command.Model);

            var estimation = await _generationRuntimeClient.EstimateTokensAsync(effectivePrompt, cancellationToken);
            var wasReduced = false;
            var reductionStrategy = "none";

            if (!estimation.Fits)
            {
                var reduction = await _promptReductionPipeline.ReduceAsync(
                    effectivePrompt,
                    estimation.MaxAllowedInputTokens,
                    cancellationToken);

                effectivePrompt = reduction.Prompt;
                wasReduced = reduction.WasReduced;
                reductionStrategy = reduction.Strategy;

                if (wasReduced)
                {
                    GenProxyMetrics.PromptReductions.Add(1, KeyValuePair.Create<string, object?>("strategy", reductionStrategy));
                }

                estimation = await _generationRuntimeClient.EstimateTokensAsync(effectivePrompt, cancellationToken);
                if (!estimation.Fits)
                {
                    throw new PromptBudgetExceededException(
                        $"Prompt exceeds token budget after prompt reduction. Max allowed input tokens: {estimation.MaxAllowedInputTokens}.");
                }
            }

            var generation = await _generationRuntimeClient.GenerateAsync(requestId, effectivePrompt, cancellationToken);
            var response = new GeneratedResponse(
                generation.RequestId,
                command.Model,
                generation.Result,
                effectivePrompt,
                wasReduced,
                reductionStrategy,
                estimation.TokenCount,
                estimation.ContextSize,
                estimation.ReservedOutputTokens,
                estimation.MaxAllowedInputTokens,
                DateTimeOffset.UtcNow);

            requestStopwatch.Stop();
            GenProxyMetrics.RequestLatencyMs.Record(requestStopwatch.Elapsed.TotalMilliseconds);
            GenProxyMetrics.RequestsSucceeded.Add(1, KeyValuePair.Create<string, object?>("model", command.Model));

            _logger.LogInformation(
                "Completed response generation. RequestId={RequestId} Model={Model} Reduced={WasReduced} Strategy={Strategy} InputTokens={InputTokens} MaxAllowedInputTokens={MaxAllowedInputTokens} DurationMs={DurationMs}",
                response.ResponseId,
                response.Model,
                response.WasReduced,
                response.ReductionStrategy,
                response.InputTokens,
                response.MaxAllowedInputTokens,
                requestStopwatch.Elapsed.TotalMilliseconds);

            return response;
        }
        catch
        {
            requestStopwatch.Stop();
            GenProxyMetrics.RequestLatencyMs.Record(requestStopwatch.Elapsed.TotalMilliseconds);
            GenProxyMetrics.RequestsFailed.Add(1, KeyValuePair.Create<string, object?>("model", command.Model));
            throw;
        }
    }
}
