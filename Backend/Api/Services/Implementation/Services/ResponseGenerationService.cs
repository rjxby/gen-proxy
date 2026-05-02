using GenProxy.Api.Integrations.Contracts;
using GenProxy.Api.Integrations.Contracts.Models;
using GenProxy.Api.Services.Contracts;
using GenProxy.Api.Services.Contracts.Models;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.Json;

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
        var responseFormat = command.ResponseFormat;

        GenProxyMetrics.RequestsStarted.Add(1, KeyValuePair.Create<string, object?>("model", command.Model));

        try
        {
            _logger.LogInformation("Starting response generation. RequestId={RequestId} Model={Model}", requestId, command.Model);

            if (responseFormat == RequestedResponseFormat.JsonObject)
            {
                var capabilities = await _generationRuntimeClient.GetCapabilitiesAsync(cancellationToken);
                if (!capabilities.SupportsJsonObjectOutput)
                {
                    throw new ResponseFormatNotSupportedException(
                        $"The configured generation runtime does not support response_format.type '{RequestedResponseFormats.GetWireName(RequestedResponseFormat.JsonObject)}'.");
                }
            }

            var estimation = await _generationRuntimeClient.EstimateTokensAsync(effectivePrompt, cancellationToken);
            var wasReduced = false;
            var reductionStrategy = PromptReductionStrategy.None;

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
                    GenProxyMetrics.PromptReductions.Add(
                        1,
                        KeyValuePair.Create<string, object?>("strategy", PromptReductionStrategyNames.GetWireName(reductionStrategy)));
                }

                estimation = await _generationRuntimeClient.EstimateTokensAsync(effectivePrompt, cancellationToken);
                if (!estimation.Fits)
                {
                    throw new PromptBudgetExceededException(
                        $"Prompt exceeds token budget after prompt reduction. Max allowed input tokens: {estimation.MaxAllowedInputTokens}.");
                }
            }

            var generation = await _generationRuntimeClient.GenerateAsync(
                requestId,
                effectivePrompt,
                new LlamaGenerationOptions(
                    MapResponseFormat(responseFormat),
                    command.MaxOutputTokens,
                    command.Temperature,
                    command.TopP),
                cancellationToken);

            if (responseFormat == RequestedResponseFormat.JsonObject)
            {
                EnsureJsonObjectResponseSatisfied(generation.Content, generation.RuntimeTrace);
            }

            var response = new GeneratedResponse(
                generation.RequestId,
                string.IsNullOrWhiteSpace(generation.Model) ? command.Model : generation.Model,
                generation.Content,
                effectivePrompt,
                wasReduced,
                reductionStrategy,
                generation.Usage?.InputTokens ?? estimation.TokenCount,
                generation.Usage?.OutputTokens,
                generation.Usage?.TotalTokens,
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
                PromptReductionStrategyNames.GetWireName(response.ReductionStrategy),
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

    private static void EnsureJsonObjectResponseSatisfied(string content, LlamaRuntimeTrace? runtimeTrace)
    {
        if (runtimeTrace is not { StructuredOutputApplied: true, StructuredOutputSatisfied: true })
        {
            throw new StructuredOutputNotSatisfiedException(
                "The generation runtime did not satisfy the requested structured output.");
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new StructuredOutputNotSatisfiedException(
                    "The generation runtime did not return a valid JSON object.");
            }
        }
        catch (JsonException)
        {
            throw new StructuredOutputNotSatisfiedException(
                "The generation runtime did not return a valid JSON object.");
        }
    }

    private static LlamaResponseFormatType? MapResponseFormat(RequestedResponseFormat? responseFormat)
    {
        return responseFormat switch
        {
            RequestedResponseFormat.Text => LlamaResponseFormatType.Text,
            RequestedResponseFormat.JsonObject => LlamaResponseFormatType.JsonObject,
            null => null,
            _ => null
        };
    }
}
