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
            GenProxyMetrics.UpstreamLatencyMs.Record(
                stopwatch.Elapsed.TotalMilliseconds,
                KeyValuePair.Create<string, object?>("operation", "estimate_tokens"),
                KeyValuePair.Create<string, object?>("runtime", _runtimeName));
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
            GenProxyMetrics.UpstreamFailures.Add(1, KeyValuePair.Create<string, object?>("runtime", _runtimeName));
            var promptBudgetException = TryCreatePromptBudgetExceededException(exception);
            if (promptBudgetException is not null)
            {
                throw promptBudgetException;
            }

            throw CreateUpstreamException("estimate tokens", exception);
        }
        catch (HttpRequestException exception)
        {
            stopwatch.Stop();
            GenProxyMetrics.UpstreamFailures.Add(1, KeyValuePair.Create<string, object?>("runtime", _runtimeName));
            throw CreateUpstreamException("estimate tokens", exception);
        }
    }

    public async Task<LlamaGenerationResult> GenerateAsync(string requestId, string prompt, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var reply = await _transport.GenerateAsync(
                new GenerateRequest
                {
                    RequestId = requestId,
                    Prompt = prompt
                },
                cancellationToken: cancellationToken);

            stopwatch.Stop();
            GenProxyMetrics.UpstreamLatencyMs.Record(
                stopwatch.Elapsed.TotalMilliseconds,
                KeyValuePair.Create<string, object?>("operation", "generate"),
                KeyValuePair.Create<string, object?>("runtime", _runtimeName));
            _logger.LogInformation(
                "Upstream generation completed. Runtime={Runtime} RequestId={RequestId} DurationMs={DurationMs}",
                _runtimeName,
                requestId,
                stopwatch.Elapsed.TotalMilliseconds);

            return new LlamaGenerationResult(reply.RequestId, reply.Result);
        }
        catch (RpcException exception)
        {
            stopwatch.Stop();
            GenProxyMetrics.UpstreamFailures.Add(1, KeyValuePair.Create<string, object?>("runtime", _runtimeName));
            var promptBudgetException = TryCreatePromptBudgetExceededException(exception);
            if (promptBudgetException is not null)
            {
                throw promptBudgetException;
            }

            throw CreateUpstreamException("generate", exception);
        }
        catch (HttpRequestException exception)
        {
            stopwatch.Stop();
            GenProxyMetrics.UpstreamFailures.Add(1, KeyValuePair.Create<string, object?>("runtime", _runtimeName));
            throw CreateUpstreamException("generate", exception);
        }
    }

    private UpstreamRuntimeException CreateUpstreamException(string operation, Exception exception)
    {
        _logger.LogWarning(exception, "Upstream runtime call failed. Runtime={Runtime} Operation={Operation}", _runtimeName, operation);
        return new UpstreamRuntimeException($"Failed to {operation} against the {_runtimeName} runtime.", exception);
    }

    private UpstreamPromptBudgetExceededException? TryCreatePromptBudgetExceededException(RpcException exception)
    {
        if (_runtimeName == "prompt_reducer" &&
            exception.StatusCode == StatusCode.InvalidArgument &&
            exception.Status.Detail.Contains(PromptBudgetExceededMarker, StringComparison.Ordinal))
        {
            _logger.LogInformation(
                "Upstream runtime rejected prompt due to token budget. Runtime={Runtime}",
                _runtimeName);
            return new UpstreamPromptBudgetExceededException(exception.Status.Detail, exception);
        }

        return null;
    }
}
