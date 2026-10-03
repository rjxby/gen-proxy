using Grpc.Net.Client;
using LlamaRuntime.Presentation.Grpc;
using System.Net.Http;
using GenProxy.Api.Integrations.Contracts.Configuration;

namespace GenProxy.Api.Integrations.Implementation.Clients;

internal interface IGrpcLlamaTransport
{
    Task<EstimateTokensReply> EstimateTokensAsync(EstimateTokensRequest request, CancellationToken cancellationToken);

    Task<GetCapabilitiesReply> GetCapabilitiesAsync(GetCapabilitiesRequest request, CancellationToken cancellationToken);

    Task<GenerateReply> GenerateAsync(GenerateRequest request, CancellationToken cancellationToken);
}

internal sealed class GrpcLlamaTransport : IGrpcLlamaTransport
{
    internal const string ApiKeyHeaderName = "x-api-key";

    private readonly Generator.GeneratorClient _client;
    private readonly RuntimeTimeoutOptions _timeouts;

    public GrpcLlamaTransport(string address, string? apiKey, RuntimeTimeoutOptions? timeouts = null)
    {
        _timeouts = timeouts ?? new RuntimeTimeoutOptions();
        ValidateTimeouts(_timeouts);
        var httpClient = CreateHttpClient(address, apiKey);

        var channel = httpClient is null
            ? GrpcChannel.ForAddress(address)
            : GrpcChannel.ForAddress(
                address,
                new GrpcChannelOptions
                {
                    HttpClient = httpClient
                });

        _client = new Generator.GeneratorClient(channel);
    }

    internal GrpcLlamaTransport(Generator.GeneratorClient client, RuntimeTimeoutOptions timeouts)
    {
        ValidateTimeouts(timeouts);
        _client = client;
        _timeouts = timeouts;
    }

    private static void ValidateTimeouts(RuntimeTimeoutOptions timeouts)
    {
        if (!timeouts.IsValid())
        {
            throw new ArgumentOutOfRangeException(nameof(timeouts), "Runtime timeouts must be greater than zero and no longer than one day.");
        }
    }

    internal static HttpClient? CreateHttpClient(string address, string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        var httpClient = new HttpClient
        {
            // Each gRPC operation owns its deadline, including generation calls longer than 100 seconds.
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = new Version(2, 0),
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher
        };

        httpClient.DefaultRequestHeaders.Add(ApiKeyHeaderName, apiKey);

        return httpClient;
    }

    public async Task<EstimateTokensReply> EstimateTokensAsync(EstimateTokensRequest request, CancellationToken cancellationToken)
    {
        return await _client.EstimateTokensAsync(request, deadline: DateTime.UtcNow.Add(_timeouts.EstimateTokens), cancellationToken: cancellationToken);
    }

    public async Task<GetCapabilitiesReply> GetCapabilitiesAsync(GetCapabilitiesRequest request, CancellationToken cancellationToken)
    {
        return await _client.GetCapabilitiesAsync(request, deadline: DateTime.UtcNow.Add(_timeouts.GetCapabilities), cancellationToken: cancellationToken);
    }

    public async Task<GenerateReply> GenerateAsync(GenerateRequest request, CancellationToken cancellationToken)
    {
        return await _client.GenerateAsync(request, deadline: DateTime.UtcNow.Add(_timeouts.Generate), cancellationToken: cancellationToken);
    }
}
