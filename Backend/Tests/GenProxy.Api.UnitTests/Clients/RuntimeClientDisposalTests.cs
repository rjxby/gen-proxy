using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using GenProxy.Api.Host.Configurations;
using GenProxy.Api.Integrations.Contracts;
using GenProxy.Api.Integrations.Contracts.Configuration;
using GenProxy.Api.Integrations.Implementation.Clients;
using Grpc.Net.Client;
using LlamaRuntime.Presentation.Grpc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GenProxy.Api.UnitTests;

public class RuntimeClientDisposalTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("runtime-key")]
    public async Task Transport_Dispose_ClosesCreatedChannel(string? apiKey)
    {
        using var transport = new GrpcLlamaTransport("https://runtime.test", apiKey);

        transport.Dispose();
        transport.Dispose();

        Func<Task> call = () => transport.GetCapabilitiesAsync(new GetCapabilitiesRequest(), CancellationToken.None);
        await call.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task Channel_Dispose_ClosesSuppliedHttpClientAndHandlerOnce()
    {
        var handler = new RecordingHttpHandler();
        var httpClient = new HttpClient(handler);
        using var channel = GrpcLlamaTransport.CreateChannel("https://runtime.test", httpClient);

        channel.Dispose();
        channel.Dispose();

        handler.DisposeCount.Should().Be(1);
        Func<Task> call = () => httpClient.GetAsync("https://runtime.test");
        await call.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task Channel_WhenConstructionFails_ClosesSuppliedHttpClientAndHandler()
    {
        var handler = new RecordingHttpHandler();
        var httpClient = new HttpClient(handler);

        Action create = () => GrpcLlamaTransport.CreateChannel("invalid address", httpClient);
        create.Should().Throw<UriFormatException>();

        handler.DisposeCount.Should().Be(1);
        Func<Task> call = () => httpClient.GetAsync("https://runtime.test");
        await call.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task Transport_WithInjectedGeneratedClient_BorrowsChannelAndHttpClient()
    {
        var handler = new RecordingHttpHandler();
        using var httpClient = new HttpClient(handler);
        using var channel = GrpcChannel.ForAddress("https://runtime.test", new GrpcChannelOptions
        {
            HttpClient = httpClient,
            DisposeHttpClient = true
        });
        using var transport = new GrpcLlamaTransport(new Generator.GeneratorClient(channel), new RuntimeTimeoutOptions());

        transport.Dispose();
        await transport.GetCapabilitiesAsync(new GetCapabilitiesRequest(), CancellationToken.None);

        handler.RequestCount.Should().Be(1);
        handler.DisposeCount.Should().Be(0);
        channel.Dispose();
        handler.DisposeCount.Should().Be(1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OwnedWrapper_Dispose_ClosesInnerTransportOnce(bool generation)
    {
        var transport = new RecordingTransport();
        var runtime = new GrpcLlamaRuntimeClient("test", NullLogger<GrpcLlamaRuntimeClient>.Instance, transport, ownsTransport: true);
        using var wrapper = CreateWrapper(runtime, generation, ownsInner: true);

        wrapper.Dispose();
        wrapper.Dispose();
        runtime.Dispose();

        transport.DisposeCount.Should().Be(1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BorrowedWrapper_Dispose_LeavesInnerClientForItsOwner(bool generation)
    {
        var transport = new RecordingTransport();
        using var runtime = new GrpcLlamaRuntimeClient("test", NullLogger<GrpcLlamaRuntimeClient>.Instance, transport, ownsTransport: true);
        using var wrapper = CreateWrapper(runtime, generation, ownsInner: false);

        wrapper.Dispose();
        transport.DisposeCount.Should().Be(0);

        runtime.Dispose();
        transport.DisposeCount.Should().Be(1);
    }

    [Fact]
    public void Runtime_WithInjectedTransport_BorrowsTransport()
    {
        var transport = new RecordingTransport();
        using var runtime = new GrpcLlamaRuntimeClient("test", NullLogger<GrpcLlamaRuntimeClient>.Instance, transport);

        runtime.Dispose();
        transport.DisposeCount.Should().Be(0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("runtime-key")]
    public async Task ServiceProvider_Dispose_ClosesBothConfiguredRuntimeChannels(string? apiKey)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["GenerationRuntime:Address"] = "https://generation.test",
            ["GenerationRuntime:ApiKey"] = apiKey,
            ["PromptReducerRuntime:Enabled"] = "true",
            ["PromptReducerRuntime:Address"] = "https://reducer.test",
            ["PromptReducerRuntime:ApiKey"] = apiKey
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIntegrationLayer(configuration);
        using var provider = services.BuildServiceProvider();
        var generation = provider.GetRequiredService<IGenerationRuntimeClient>();
        var reducer = provider.GetRequiredService<IPromptReducerRuntimeClient>();

        provider.Dispose();

        Func<Task> generationCall = () => generation.GetCapabilitiesAsync(CancellationToken.None);
        Func<Task> reducerCall = () => reducer.GetCapabilitiesAsync(CancellationToken.None);
        await generationCall.Should().ThrowAsync<ObjectDisposedException>();
        await reducerCall.Should().ThrowAsync<ObjectDisposedException>();
    }

    private static IDisposable CreateWrapper(ILlamaRuntimeClient runtime, bool generation, bool ownsInner) =>
        generation
            ? new GenerationRuntimeClient(runtime, ownsInner)
            : new PromptReducerRuntimeClient(runtime, ownsInner);

    private sealed class RecordingTransport : IGrpcLlamaTransport, IDisposable
    {
        public int DisposeCount { get; private set; }

        public void Dispose() => DisposeCount++;

        public Task<EstimateTokensReply> EstimateTokensAsync(EstimateTokensRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new EstimateTokensReply());

        public Task<GetCapabilitiesReply> GetCapabilitiesAsync(GetCapabilitiesRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new GetCapabilitiesReply());

        public Task<GenerateReply> GenerateAsync(GenerateRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new GenerateReply());
    }

    private sealed class RecordingHttpHandler : HttpMessageHandler
    {
        public int DisposeCount { get; private set; }
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Version = new Version(2, 0),
                Content = new ByteArrayContent([0, 0, 0, 0, 0])
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
            response.TrailingHeaders.Add("grpc-status", "0");
            return Task.FromResult(response);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeCount++;
            }

            base.Dispose(disposing);
        }
    }
}
