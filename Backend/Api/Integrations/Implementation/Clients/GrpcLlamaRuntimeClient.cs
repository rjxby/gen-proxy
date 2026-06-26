using GenProxy.Api.Integrations.Contracts;
using GenProxy.Api.Integrations.Contracts.Models;
using Grpc.Core;
using LlamaRuntime.Presentation.Grpc;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace GenProxy.Api.Integrations.Implementation.Clients;

public sealed class GrpcLlamaRuntimeClient : ILlamaRuntimeClient
{
    private const string PromptBudgetExceededMarker = "Prompt exceeds input budget";
    private const string RuntimeErrorCodeTrailerName = "runtime-error-code";
    private const string StructuredOutputNotSatisfiedDetail = "Inference did not return a valid JSON object.";
    private const string InvalidArgumentCode = "invalid_argument";
    private const string UnsupportedGenerationOverridesCode = "unsupported_generation_overrides";
    private readonly IGrpcLlamaTransport _transport;
    private readonly string _runtimeName;
    private readonly ILogger<GrpcLlamaRuntimeClient> _logger;

    public GrpcLlamaRuntimeClient(string runtimeName, string address, ILogger<GrpcLlamaRuntimeClient> logger, string? apiKey = null)
        : this(runtimeName, logger, new GrpcLlamaTransport(address, apiKey))
    {
    }

    internal GrpcLlamaRuntimeClient(string runtimeName, ILogger<GrpcLlamaRuntimeClient> logger, IGrpcLlamaTransport transport)
    {
        _runtimeName = runtimeName;
        _logger = logger;
        _transport = transport;
    }

    public async Task<TokenEstimation> EstimateTokensAsync(string prompt, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var reply = await _transport.EstimateTokensAsync(
                new EstimateTokensRequest { Prompt = prompt },
                cancellationToken: cancellationToken);

            stopwatch.Stop();
            RecordUpstreamLatency("estimate_tokens", stopwatch.Elapsed);
            _logger.LogInformation(
                "Upstream token estimation completed. Runtime={Runtime} TokenCount={TokenCount} Fits={Fits} DurationMs={DurationMs}",
                _runtimeName,
                reply.TokenCount,
                reply.Fits,
                stopwatch.Elapsed.TotalMilliseconds);

            return new TokenEstimation(
                reply.TokenCount,
                reply.ContextSize,
                reply.ReservedOutputTokens,
                reply.MaxAllowedInputTokens,
                reply.Fits);
        }
        catch (RpcException exception)
        {
            stopwatch.Stop();
            throw HandleRpcException("estimate tokens", exception, TryCreatePromptBudgetExceededException);
        }
        catch (HttpRequestException exception)
        {
            stopwatch.Stop();
            RecordUpstreamFailure();
            throw CreateUpstreamException("estimate tokens", exception);
        }
    }

    public async Task<LlamaCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var reply = await _transport.GetCapabilitiesAsync(
                new GetCapabilitiesRequest(),
                cancellationToken);

            stopwatch.Stop();
            RecordUpstreamLatency("get_capabilities", stopwatch.Elapsed);

            var capabilities = new LlamaCapabilities(
                reply.ModelId,
                reply.ContextSize,
                reply.SupportsStructuredOutput,
                reply.SupportsJsonOutput,
                reply.SupportsSpeculativeDecoding,
                reply.TokenizerFamily);

            _logger.LogInformation(
                "Upstream capability fetch completed. Runtime={Runtime} ModelId={ModelId} StructuredOutput={SupportsStructuredOutput} JsonOutput={SupportsJsonOutput} DurationMs={DurationMs}",
                _runtimeName,
                capabilities.ModelId,
                capabilities.SupportsStructuredOutput,
                capabilities.SupportsJsonOutput,
                stopwatch.Elapsed.TotalMilliseconds);

            return capabilities;
        }
        catch (RpcException exception)
        {
            stopwatch.Stop();
            throw HandleRpcException("get capabilities", exception);
        }
        catch (HttpRequestException exception)
        {
            stopwatch.Stop();
            RecordUpstreamFailure();
            throw CreateUpstreamException("get capabilities", exception);
        }
    }

    public async Task<LlamaGenerationResult> GenerateAsync(
        string requestId,
        string prompt,
        LlamaGenerationOptions? options,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var request = new GenerateRequest
            {
                RequestId = requestId,
                Prompt = prompt
            };

            if (options?.ResponseFormat is LlamaResponseFormatType configuredResponseFormat)
            {
                request.ResponseFormat = new ResponseFormat
                {
                    Type = configuredResponseFormat switch
                    {
                        LlamaResponseFormatType.Text => "text",
                        LlamaResponseFormatType.JsonSchema => "json",
                        _ => throw new InvalidOperationException($"Unsupported runtime response format '{configuredResponseFormat}'.")
                    }
                };

                if (configuredResponseFormat == LlamaResponseFormatType.JsonSchema &&
                    !string.IsNullOrWhiteSpace(options.JsonSchema))
                {
                    request.ResponseFormat.JsonSchema = options.JsonSchema;
                }
            }

            if (options?.MaxOutputTokens is not null ||
                options?.Temperature is not null ||
                options?.TopP is not null)
            {
                request.Generation = new GenerationOptions();

                if (options?.MaxOutputTokens is int configuredMaxOutputTokens)
                {
                    request.Generation.MaxOutputTokens = configuredMaxOutputTokens;
                }

                if (options?.Temperature is float configuredTemperature)
                {
                    request.Generation.Temperature = configuredTemperature;
                }

                if (options?.TopP is float configuredTopP)
                {
                    request.Generation.TopP = configuredTopP;
                }
            }

            var reply = await _transport.GenerateAsync(
                request,
                cancellationToken: cancellationToken);

            stopwatch.Stop();
            RecordUpstreamLatency("generate", stopwatch.Elapsed);
            _logger.LogInformation(
                "Upstream generation completed. Runtime={Runtime} RequestId={RequestId} DurationMs={DurationMs}",
                _runtimeName,
                requestId,
                stopwatch.Elapsed.TotalMilliseconds);

            return new LlamaGenerationResult(
                reply.RequestId,
                reply.Model,
                reply.Content,
                reply.Usage is null
                    ? null
                    : new LlamaUsage(reply.Usage.InputTokens, reply.Usage.OutputTokens, reply.Usage.TotalTokens),
                reply.RuntimeTrace is null
                    ? null
                    : new LlamaRuntimeTrace(
                        reply.RuntimeTrace.StructuredOutputApplied,
                        reply.RuntimeTrace.StructuredOutputSatisfied,
                        reply.RuntimeTrace.SpeculativeDecodingUsed));
        }
        catch (RpcException exception)
        {
            stopwatch.Stop();
            throw HandleRpcException(
                "generate",
                exception,
                TryCreatePromptBudgetExceededException,
                TryCreateUnsupportedGenerationOverridesException,
                TryCreateInvalidArgumentException,
                TryCreateStructuredOutputNotSatisfiedException);
        }
        catch (HttpRequestException exception)
        {
            stopwatch.Stop();
            RecordUpstreamFailure();
            throw CreateUpstreamException("generate", exception);
        }
    }

    private void RecordUpstreamLatency(string operation, TimeSpan elapsed)
    {
        GenProxyMetrics.UpstreamLatencyMs.Record(
            elapsed.TotalMilliseconds,
            KeyValuePair.Create<string, object?>("operation", operation),
            KeyValuePair.Create<string, object?>("runtime", _runtimeName));
    }

    private void RecordUpstreamFailure()
    {
        GenProxyMetrics.UpstreamFailures.Add(1, KeyValuePair.Create<string, object?>("runtime", _runtimeName));
    }

    private Exception HandleRpcException(
        string operation,
        RpcException exception,
        params Func<RpcException, Exception?>[] translators)
    {
        RecordUpstreamFailure();

        foreach (var translator in translators)
        {
            var translatedException = translator(exception);
            if (translatedException is not null)
            {
                return translatedException;
            }
        }

        return CreateUpstreamException(operation, exception);
    }

    private LlamaRuntimeCallException CreateUpstreamException(string operation, Exception exception)
    {
        _logger.LogWarning(exception, "Upstream runtime call failed. Runtime={Runtime} Operation={Operation}", _runtimeName, operation);
        return new LlamaRuntimeCallException($"Failed to {operation} against the {_runtimeName} runtime.", exception);
    }

    private LlamaRuntimePromptBudgetExceededException? TryCreatePromptBudgetExceededException(RpcException exception)
    {
        if (_runtimeName == "prompt_reducer" &&
            exception.StatusCode == StatusCode.InvalidArgument &&
            exception.Status.Detail.Contains(PromptBudgetExceededMarker, StringComparison.Ordinal))
        {
            _logger.LogInformation(
                "Upstream runtime rejected prompt due to token budget. Runtime={Runtime}",
                _runtimeName);
            return new LlamaRuntimePromptBudgetExceededException(exception.Status.Detail, exception);
        }

        return null;
    }

    private LlamaRuntimeUnsupportedGenerationOverridesException? TryCreateUnsupportedGenerationOverridesException(RpcException exception)
    {
        if (exception.StatusCode != StatusCode.InvalidArgument)
        {
            return null;
        }

        var errorCode = exception.Trailers
            .FirstOrDefault(entry => string.Equals(entry.Key, RuntimeErrorCodeTrailerName, StringComparison.Ordinal))
            ?.Value;

        if (!string.Equals(errorCode, UnsupportedGenerationOverridesCode, StringComparison.Ordinal))
        {
            return null;
        }

        _logger.LogInformation(
            "Upstream runtime rejected request-level generation overrides. Runtime={Runtime}",
            _runtimeName);

        return new LlamaRuntimeUnsupportedGenerationOverridesException(exception.Status.Detail, exception);
    }

    private LlamaRuntimeInvalidArgumentException? TryCreateInvalidArgumentException(RpcException exception)
    {
        if (exception.StatusCode != StatusCode.InvalidArgument)
        {
            return null;
        }

        var errorCode = exception.Trailers
            .FirstOrDefault(entry => string.Equals(entry.Key, RuntimeErrorCodeTrailerName, StringComparison.Ordinal))
            ?.Value;

        if (!string.Equals(errorCode, InvalidArgumentCode, StringComparison.Ordinal))
        {
            return null;
        }

        _logger.LogInformation(
            "Upstream runtime rejected request argument. Runtime={Runtime}",
            _runtimeName);

        return new LlamaRuntimeInvalidArgumentException(exception.Status.Detail, exception);
    }

    private LlamaRuntimeStructuredOutputNotSatisfiedException? TryCreateStructuredOutputNotSatisfiedException(RpcException exception)
    {
        if (exception.StatusCode != StatusCode.Internal ||
            !string.Equals(exception.Status.Detail, StructuredOutputNotSatisfiedDetail, StringComparison.Ordinal))
        {
            return null;
        }

        _logger.LogInformation(
            "Upstream runtime did not satisfy structured output. Runtime={Runtime}",
            _runtimeName);

        return new LlamaRuntimeStructuredOutputNotSatisfiedException(exception.Status.Detail, exception);
    }
}
