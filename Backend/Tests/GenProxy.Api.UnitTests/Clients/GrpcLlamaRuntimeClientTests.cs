using FluentAssertions;
using GenProxy.Api.Integrations.Implementation.Clients;
using GenProxy.Api.Integrations.Implementation.Configuration;
using GenProxy.Api.UnitTests.Testing;
using GenProxy.Api.Services.Implementation.Configuration;
using Grpc.Core;
using LlamaRuntime.Presentation.Grpc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace GenProxy.Api.UnitTests;

public class GrpcLlamaRuntimeClientTests
{
    private const string RuntimeName = "test";

    [Fact]
    public void CreateHttpClient_WithApiKey_AddsXApiKeyHeader()
    {
        using var httpClient = GrpcLlamaTransport.CreateHttpClient("https://runtime.test", "runtime-key");

        httpClient.Should().NotBeNull();
        httpClient.DefaultRequestHeaders.Contains(GrpcLlamaTransport.ApiKeyHeaderName).Should().BeTrue();
        httpClient.DefaultRequestHeaders.GetValues(GrpcLlamaTransport.ApiKeyHeaderName)
            .Should()
            .ContainSingle("runtime-key");
    }

    [Fact]
    public void CreateHttpClient_WithoutApiKey_DoesNotAddXApiKeyHeader()
    {
        var httpClient = GrpcLlamaTransport.CreateHttpClient("https://runtime.test", null);

        httpClient.Should().BeNull();
    }

    [Fact]
    public void CreateHttpClient_WithLoopbackHttpsBypassEnabled_ReturnsHttpClientWithoutApiKey()
    {
        var httpClient = GrpcLlamaTransport.CreateHttpClient("https://localhost:50051", null);

        httpClient.Should().BeNull();
    }

    [Fact]
    public async Task EstimateTokensAsync_MapsTransportReply()
    {
        var transport = new FakeGrpcTransport
        {
            EstimateReply = new EstimateTokensReply
            {
                TokenCount = 42,
                ContextSize = 8192,
                ReservedOutputTokens = 512,
                MaxAllowedInputTokens = 7680,
                Fits = true
            }
        };
        var client = new GrpcLlamaRuntimeClient(RuntimeName, NullLogger<GrpcLlamaRuntimeClient>.Instance, transport);

        var result = await client.EstimateTokensAsync("prompt", CancellationToken.None);

        transport.LastEstimateRequest.Should().NotBeNull();
        transport.LastEstimateRequest!.Prompt.Should().Be("prompt");
        result.TokenCount.Should().Be(42);
        result.ContextSize.Should().Be(8192);
        result.ReservedOutputTokens.Should().Be(512);
        result.MaxAllowedInputTokens.Should().Be(7680);
        result.Fits.Should().BeTrue();
    }

    [Fact]
    public async Task GenerateAsync_MapsTransportReply()
    {
        var transport = new FakeGrpcTransport
        {
            GenerateReply = new GenerateReply
            {
                RequestId = "req_123",
                Result = "generated text"
            }
        };
        var client = new GrpcLlamaRuntimeClient(RuntimeName, NullLogger<GrpcLlamaRuntimeClient>.Instance, transport);

        var result = await client.GenerateAsync("req_123", "prompt", CancellationToken.None);

        transport.LastGenerateRequest.Should().NotBeNull();
        transport.LastGenerateRequest!.RequestId.Should().Be("req_123");
        transport.LastGenerateRequest.Prompt.Should().Be("prompt");
        result.RequestId.Should().Be("req_123");
        result.Result.Should().Be("generated text");
    }

    [Fact]
    public async Task EstimateTokensAsync_WhenTransportThrowsRpcException_ThrowsUpstreamRuntimeException()
    {
        var transport = new FakeGrpcTransport
        {
            EstimateException = new RpcException(new Status(StatusCode.Unavailable, "offline"))
        };
        var client = new GrpcLlamaRuntimeClient(RuntimeName, NullLogger<GrpcLlamaRuntimeClient>.Instance, transport);

        var act = async () => await client.EstimateTokensAsync("prompt", CancellationToken.None);

        var exception = await act.Should().ThrowAsync<GenProxy.Api.Integrations.Contracts.UpstreamRuntimeException>();
        exception.Which.Message.Should().Be("Failed to estimate tokens against the test runtime.");
        exception.Which.InnerException.Should().BeOfType<RpcException>();
    }

    [Fact]
    public async Task GenerateAsync_WhenPromptReducerReportsBudgetExceeded_ThrowsUpstreamPromptBudgetExceededException()
    {
        var transport = new FakeGrpcTransport
        {
            GenerateException = new RpcException(new Status(StatusCode.InvalidArgument, "Prompt exceeds input budget: 6250 tokens > 3584 allowed."))
        };
        var client = new GrpcLlamaRuntimeClient("prompt_reducer", NullLogger<GrpcLlamaRuntimeClient>.Instance, transport);

        var act = async () => await client.GenerateAsync("req_123", "prompt", CancellationToken.None);

        var exception = await act.Should().ThrowAsync<GenProxy.Api.Integrations.Contracts.UpstreamPromptBudgetExceededException>();
        exception.Which.Message.Should().Contain("Prompt exceeds input budget");
        exception.Which.InnerException.Should().BeOfType<RpcException>();
    }

    [Fact]
    public async Task GenerateAsync_WhenNonBudgetInvalidArgument_ThrowsUpstreamRuntimeException()
    {
        var transport = new FakeGrpcTransport
        {
            GenerateException = new RpcException(new Status(StatusCode.InvalidArgument, "schema mismatch"))
        };
        var client = new GrpcLlamaRuntimeClient("prompt_reducer", NullLogger<GrpcLlamaRuntimeClient>.Instance, transport);

        var act = async () => await client.GenerateAsync("req_123", "prompt", CancellationToken.None);

        var exception = await act.Should().ThrowAsync<GenProxy.Api.Integrations.Contracts.UpstreamRuntimeException>();
        exception.Which.Message.Should().Be("Failed to generate against the prompt_reducer runtime.");
        exception.Which.InnerException.Should().BeOfType<RpcException>();
    }

    [Fact]
    public async Task GenerateAsync_WhenPromptReducerReportsBudgetExceeded_LogsWithoutExceptionObject()
    {
        var transport = new FakeGrpcTransport
        {
            GenerateException = new RpcException(new Status(StatusCode.InvalidArgument, "Prompt exceeds input budget: 6250 tokens > 3584 allowed."))
        };
        var logger = new TestLogger<GrpcLlamaRuntimeClient>();
        var client = new GrpcLlamaRuntimeClient("prompt_reducer", logger, transport);

        var act = async () => await client.GenerateAsync("req_123", "prompt", CancellationToken.None);

        await act.Should().ThrowAsync<GenProxy.Api.Integrations.Contracts.UpstreamPromptBudgetExceededException>();
        logger.Entries.Should().ContainSingle();
        logger.Entries[0].Level.Should().Be(LogLevel.Information);
        logger.Entries[0].Exception.Should().BeNull();
        logger.Entries[0].Message.Should().Contain("Upstream runtime rejected prompt due to token budget.");
    }

    [Fact]
    public async Task GenerateAsync_WhenTransportThrowsHttpRequestException_ThrowsUpstreamRuntimeException()
    {
        var transport = new FakeGrpcTransport
        {
            GenerateException = new HttpRequestException("offline")
        };
        var client = new GrpcLlamaRuntimeClient(RuntimeName, NullLogger<GrpcLlamaRuntimeClient>.Instance, transport);

        var act = async () => await client.GenerateAsync("req_123", "prompt", CancellationToken.None);

        var exception = await act.Should().ThrowAsync<GenProxy.Api.Integrations.Contracts.UpstreamRuntimeException>();
        exception.Which.Message.Should().Be("Failed to generate against the test runtime.");
        exception.Which.InnerException.Should().BeOfType<HttpRequestException>();
    }

    [Fact]
    public void GenerationRuntimeOptions_BindsApiKeyFromConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{GenerationRuntimeOptions.SectionName}:Address"] = "https://generation.test",
                [$"{GenerationRuntimeOptions.SectionName}:ApiKey"] = "generation-key"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddOptions<GenerationRuntimeOptions>()
            .Bind(configuration.GetSection(GenerationRuntimeOptions.SectionName))
            .Validate(options => Uri.TryCreate(options.Address, UriKind.Absolute, out var uri) &&
                uri.Scheme == Uri.UriSchemeHttps)
            .ValidateOnStart();

        using var serviceProvider = services.BuildServiceProvider();
        var options = serviceProvider.GetRequiredService<IOptions<GenerationRuntimeOptions>>().Value;

        options.Address.Should().Be("https://generation.test");
        options.ApiKey.Should().Be("generation-key");
    }

    [Fact]
    public void PromptReducerRuntimeOptions_BindsApiKeyFromConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{PromptReducerRuntimeOptions.SectionName}:Address"] = "https://reducer.test",
                [$"{PromptReducerRuntimeOptions.SectionName}:ApiKey"] = "reducer-key"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddOptions<PromptReducerRuntimeOptions>()
            .Bind(configuration.GetSection(PromptReducerRuntimeOptions.SectionName))
            .Validate(options => Uri.TryCreate(options.Address, UriKind.Absolute, out var uri) &&
                uri.Scheme == Uri.UriSchemeHttps)
            .ValidateOnStart();

        using var serviceProvider = services.BuildServiceProvider();
        var options = serviceProvider.GetRequiredService<IOptions<PromptReducerRuntimeOptions>>().Value;

        options.Address.Should().Be("https://reducer.test");
        options.ApiKey.Should().Be("reducer-key");
    }

    [Fact]
    public void PromptReductionOptions_BindsUsePromptReducerRuntimeFromConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{PromptReductionOptions.SectionName}:UsePromptReducerRuntime"] = "false"
            })
            .Build();

        var services = new ServiceCollection();
        services.Configure<PromptReductionOptions>(configuration.GetSection(PromptReductionOptions.SectionName));

        using var serviceProvider = services.BuildServiceProvider();
        var options = serviceProvider.GetRequiredService<IOptions<PromptReductionOptions>>().Value;

        options.UsePromptReducerRuntime.Should().BeFalse();
    }

    [Fact]
    public void RuntimeClientConstructors_AcceptInjectedInnerClient()
    {
        var innerClient = new GrpcLlamaRuntimeClient("generation", "https://localhost:50051", NullLogger<GrpcLlamaRuntimeClient>.Instance);

        new GenerationRuntimeClient(innerClient).Should().NotBeNull();
        new PromptReducerRuntimeClient(innerClient).Should().NotBeNull();
    }

    private sealed class FakeGrpcTransport : IGrpcLlamaTransport
    {
        public EstimateTokensReply? EstimateReply { get; init; }

        public GenerateReply? GenerateReply { get; init; }

        public Exception? EstimateException { get; init; }

        public Exception? GenerateException { get; init; }

        public EstimateTokensRequest? LastEstimateRequest { get; private set; }

        public GenerateRequest? LastGenerateRequest { get; private set; }

        public Task<EstimateTokensReply> EstimateTokensAsync(EstimateTokensRequest request, CancellationToken cancellationToken)
        {
            LastEstimateRequest = request;

            return EstimateException is not null
                ? Task.FromException<EstimateTokensReply>(EstimateException)
                : Task.FromResult(EstimateReply ?? new EstimateTokensReply());
        }

        public Task<GenerateReply> GenerateAsync(GenerateRequest request, CancellationToken cancellationToken)
        {
            LastGenerateRequest = request;

            return GenerateException is not null
                ? Task.FromException<GenerateReply>(GenerateException)
                : Task.FromResult(GenerateReply ?? new GenerateReply());
        }
    }
}
