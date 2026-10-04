using GenProxy.Api.Integrations.Contracts;
using GenProxy.Api.Integrations.Contracts.Configuration;
using GenProxy.Api.Integrations.Contracts.Models;
using Grpc.Core;
using LlamaRuntime.Presentation.Grpc;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace GenProxy.Api.Integrations.Implementation.Clients;

public sealed class GrpcLlamaRuntimeClient : ILlamaRuntimeClient, IDisposable
{
    private const string PromptBudgetExceededMarker = "Prompt exceeds input budget";
    private const string RuntimeErrorCodeTrailerName = "runtime-error-code";
    private const string StructuredOutputNotSatisfiedDetail = "Inference did not return a valid JSON object.";
    private const string StructuredOutputFailedCode = "structured_output_failed";
    private const string InvalidArgumentCode = "invalid_argument";
    private const string UnsupportedGenerationOverridesCode = "unsupported_generation_overrides";
    private readonly IGrpcLlamaTransport _transport;
    private readonly string _runtimeName;
    private readonly ILogger<GrpcLlamaRuntimeClient> _logger;
    private IDisposable? _ownedTransport;

    public GrpcLlamaRuntimeClient(string runtimeName, string address, ILogger<GrpcLlamaRuntimeClient> logger, string? apiKey = null, RuntimeTimeoutOptions? timeouts = null)
        : this(runtimeName, logger, new GrpcLlamaTransport(address, apiKey, timeouts), ownsTransport: true)
    {
    }

    internal GrpcLlamaRuntimeClient(string runtimeName, ILogger<GrpcLlamaRuntimeClient> logger, IGrpcLlamaTransport transport, bool ownsTransport = false)
    {
        _runtimeName = runtimeName;
        _logger = logger;
        _transport = transport;
        _ownedTransport = ownsTransport ? transport as IDisposable : null;
    }

    public void Dispose() => Interlocked.Exchange(ref _ownedTransport, null)?.Dispose();

    public async Task<TokenEstimation> EstimateTokensAsync(string prompt, CancellationToken cancellationToken)
    {
        var (reply, durationMs) = await CallAsync(
            "estimate_tokens",
            "estimate tokens",
            () => _transport.EstimateTokensAsync(new EstimateTokensRequest { Prompt = prompt }, cancellationToken),
            cancellationToken,
            TryCreatePromptBudgetExceededException);

        _logger.LogInformation(
            "Upstream token estimation completed. Runtime={Runtime} TokenCount={TokenCount} Fits={Fits} DurationMs={DurationMs}",
            _runtimeName,
            reply.TokenCount,
            reply.Fits,
            durationMs);

        return new TokenEstimation(
            reply.TokenCount,
            reply.ContextSize,
            reply.ReservedOutputTokens,
            reply.MaxAllowedInputTokens,
            reply.Fits);
    }

    public async Task<LlamaCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken)
    {
        var (reply, durationMs) = await CallAsync(
            "get_capabilities",
            "get capabilities",
            () => _transport.GetCapabilitiesAsync(new GetCapabilitiesRequest(), cancellationToken),
            cancellationToken);

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
            durationMs);

        return capabilities;
    }

    public async Task<LlamaGenerationResult> GenerateAsync(
        string requestId,
        string prompt,
        LlamaGenerationOptions? options,
        CancellationToken cancellationToken)
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

        var (reply, durationMs) = await CallAsync(
            "generate",
            "generate",
            () => _transport.GenerateAsync(request, cancellationToken),
            cancellationToken,
            TryCreatePromptBudgetExceededException,
            TryCreateUnsupportedGenerationOverridesException,
            TryCreateInvalidArgumentException,
            TryCreateStructuredOutputNotSatisfiedException);

        _logger.LogInformation(
            "Upstream generation completed. Runtime={Runtime} RequestId={RequestId} DurationMs={DurationMs}",
            _runtimeName,
            requestId,
            durationMs);

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

    private async Task<(TReply Reply, double DurationMs)> CallAsync<TReply>(
        string operation,
        string operationDescription,
        Func<Task<TReply>> call,
        CancellationToken cancellationToken,
        params Func<RpcException, Exception?>[] translators)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var reply = await call();
            stopwatch.Stop();
            RecordUpstreamLatency(operation, stopwatch.Elapsed);
            return (reply, stopwatch.Elapsed.TotalMilliseconds);
        }
        catch (RpcException exception)
        {
            throw HandleRpcException(operationDescription, exception, cancellationToken, translators);
        }
        catch (HttpRequestException exception)
        {
            RecordUpstreamFailure();
            throw CreateUpstreamException(operationDescription, exception);
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
        CancellationToken cancellationToken,
        params Func<RpcException, Exception?>[] translators)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return new OperationCanceledException("The runtime call was canceled by the caller.", exception, cancellationToken);
        }

        RecordUpstreamFailure();

        if (exception.StatusCode == StatusCode.DeadlineExceeded)
        {
            _logger.LogWarning("Upstream runtime call timed out. Runtime={Runtime} Operation={Operation}", _runtimeName, operation);
            return new LlamaRuntimeTimeoutException($"Timed out while attempting to {operation} against the {_runtimeName} runtime.", exception);
        }

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
        if (exception.StatusCode != StatusCode.Internal)
        {
            return null;
        }

        var errorCode = exception.Trailers
            .FirstOrDefault(entry => string.Equals(entry.Key, RuntimeErrorCodeTrailerName, StringComparison.Ordinal))
            ?.Value;

        if (!string.Equals(errorCode, StructuredOutputFailedCode, StringComparison.Ordinal) &&
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
