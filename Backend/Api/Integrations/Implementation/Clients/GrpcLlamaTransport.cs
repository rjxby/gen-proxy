using Grpc.Net.Client;
using LlamaRuntime.Presentation.Grpc;
using System.Net.Http;

namespace GenProxy.Api.Integrations.Implementation.Clients;

internal interface IGrpcLlamaTransport
{
    Task<EstimateTokensReply> EstimateTokensAsync(EstimateTokensRequest request, CancellationToken cancellationToken);

    Task<GenerateReply> GenerateAsync(GenerateRequest request, CancellationToken cancellationToken);
}

internal sealed class GrpcLlamaTransport : IGrpcLlamaTransport
{
    internal const string ApiKeyHeaderName = "x-api-key";

    private readonly Generator.GeneratorClient _client;

    public GrpcLlamaTransport(string address, string? apiKey)
    {
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

    internal static HttpClient? CreateHttpClient(string address, string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        var httpClient = new HttpClient
        {
            DefaultRequestVersion = new Version(2, 0),
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher
        };

        httpClient.DefaultRequestHeaders.Add(ApiKeyHeaderName, apiKey);

        return httpClient;
    }

    public async Task<EstimateTokensReply> EstimateTokensAsync(EstimateTokensRequest request, CancellationToken cancellationToken)
    {
        return await _client.EstimateTokensAsync(request, cancellationToken: cancellationToken);
    }

    public async Task<GenerateReply> GenerateAsync(GenerateRequest request, CancellationToken cancellationToken)
    {
        return await _client.GenerateAsync(request, cancellationToken: cancellationToken);
    }
}
