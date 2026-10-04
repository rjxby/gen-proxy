using FluentAssertions;
using GenProxy.Api.Integrations.Contracts.Configuration;
using GenProxy.Api.Integrations.Contracts;
using GenProxy.Api.Integrations.Contracts.Models;
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
using System.Diagnostics.Metrics;
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
                Model = "runtime-model",
                Content = "generated text",
                Usage = new Usage
                {
                    InputTokens = 11,
                    OutputTokens = 7,
                    TotalTokens = 18
                },
                RuntimeTrace = new RuntimeTrace
                {
                    StructuredOutputApplied = true,
                    StructuredOutputSatisfied = true,
                    SpeculativeDecodingUsed = false
                }
            }
        };
        var client = new GrpcLlamaRuntimeClient(RuntimeName, NullLogger<GrpcLlamaRuntimeClient>.Instance, transport);

        var result = await client.GenerateAsync(
            "req_123",
            "prompt",
            new LlamaGenerationOptions(
                LlamaResponseFormatType.JsonSchema,
                "{\"type\":\"object\"}",
                123,
                0.3f,
                0.8f),
            CancellationToken.None);

        transport.LastGenerateRequest.Should().NotBeNull();
        transport.LastGenerateRequest!.RequestId.Should().Be("req_123");
        transport.LastGenerateRequest.Prompt.Should().Be("prompt");
        transport.LastGenerateRequest.ResponseFormat.Type.Should().Be("json");
        transport.LastGenerateRequest.ResponseFormat.JsonSchema.Should().Be("{\"type\":\"object\"}");
        transport.LastGenerateRequest.Generation.MaxOutputTokens.Should().Be(123);
        transport.LastGenerateRequest.Generation.Temperature.Should().Be(0.3f);
        transport.LastGenerateRequest.Generation.TopP.Should().Be(0.8f);
        result.RequestId.Should().Be("req_123");
        result.Model.Should().Be("runtime-model");
        result.Content.Should().Be("generated text");
        result.Usage.Should().NotBeNull();
        result.Usage!.InputTokens.Should().Be(11);
        result.Usage.OutputTokens.Should().Be(7);
        result.Usage.TotalTokens.Should().Be(18);
        result.RuntimeTrace.Should().NotBeNull();
        result.RuntimeTrace!.StructuredOutputSatisfied.Should().BeTrue();
    }

    [Fact]
    public async Task GenerateAsync_WhenOptionsOmitted_DoesNotPopulateGenerationBlock()
    {
        var transport = new FakeGrpcTransport();
        var client = new GrpcLlamaRuntimeClient(RuntimeName, NullLogger<GrpcLlamaRuntimeClient>.Instance, transport);

        await client.GenerateAsync("req_123", "prompt", null, CancellationToken.None);

        transport.LastGenerateRequest.Should().NotBeNull();
        transport.LastGenerateRequest!.Generation.Should().BeNull();
    }

    [Fact]
    public async Task GetCapabilitiesAsync_MapsTransportReplyAndFetchesFreshResults()
    {
        var transport = new FakeGrpcTransport
        {
            CapabilitiesReply = new GetCapabilitiesReply
            {
                ModelId = "runtime-model",
                ContextSize = 8192,
                SupportsStructuredOutput = true,
                SupportsJsonOutput = true,
                SupportsSpeculativeDecoding = false,
                TokenizerFamily = "llama"
            }
        };
        var client = new GrpcLlamaRuntimeClient(RuntimeName, NullLogger<GrpcLlamaRuntimeClient>.Instance, transport);

        var firstResult = await client.GetCapabilitiesAsync(CancellationToken.None);
        var secondResult = await client.GetCapabilitiesAsync(CancellationToken.None);

        firstResult.ModelId.Should().Be("runtime-model");
        firstResult.SupportsJsonOutput.Should().BeTrue();
        secondResult.Should().BeEquivalentTo(firstResult);
        transport.GetCapabilitiesCallCount.Should().Be(2);
    }

    [Fact]
    public async Task GetCapabilitiesAsync_WhenCalledConcurrently_StartsOneFetchPerCall()
    {
        var firstReplySource = new TaskCompletionSource<GetCapabilitiesReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondReplySource = new TaskCompletionSource<GetCapabilitiesReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new FakeGrpcTransport();
        transport.CapabilityResponses.Enqueue(_ => firstReplySource.Task);
        transport.CapabilityResponses.Enqueue(_ => secondReplySource.Task);
        var client = new GrpcLlamaRuntimeClient(RuntimeName, NullLogger<GrpcLlamaRuntimeClient>.Instance, transport);

        var firstTask = client.GetCapabilitiesAsync(CancellationToken.None);
        var secondTask = client.GetCapabilitiesAsync(CancellationToken.None);

        var timeoutAt = DateTime.UtcNow.AddSeconds(1);
        while (transport.GetCapabilitiesCallCount < 2 && DateTime.UtcNow < timeoutAt)
        {
            await Task.Yield();
        }

        transport.GetCapabilitiesCallCount.Should().Be(2);

        firstReplySource.SetResult(new GetCapabilitiesReply
        {
            ModelId = "runtime-model",
            ContextSize = 8192,
            SupportsStructuredOutput = true,
            SupportsJsonOutput = true,
            SupportsSpeculativeDecoding = false,
            TokenizerFamily = "llama"
        });
        secondReplySource.SetResult(new GetCapabilitiesReply
        {
            ModelId = "runtime-model",
            ContextSize = 8192,
            SupportsStructuredOutput = true,
            SupportsJsonOutput = true,
            SupportsSpeculativeDecoding = false,
            TokenizerFamily = "llama"
        });

        var results = await Task.WhenAll(firstTask, secondTask);

        results[0].Should().BeEquivalentTo(results[1]);
        transport.GetCapabilitiesCallCount.Should().Be(2);
    }

    [Fact]
    public async Task GetCapabilitiesAsync_WhenInitialFetchFails_AllowsRetry()
    {
        var transport = new FakeGrpcTransport();
        transport.CapabilityResponses.Enqueue(_ => Task.FromException<GetCapabilitiesReply>(
            new RpcException(new Status(StatusCode.Unavailable, "offline"))));
        transport.CapabilityResponses.Enqueue(_ => Task.FromResult(new GetCapabilitiesReply
        {
            ModelId = "runtime-model",
            ContextSize = 8192,
            SupportsStructuredOutput = true,
            SupportsJsonOutput = true,
            SupportsSpeculativeDecoding = false,
            TokenizerFamily = "llama"
        }));
        var client = new GrpcLlamaRuntimeClient(RuntimeName, NullLogger<GrpcLlamaRuntimeClient>.Instance, transport);

        var firstAttempt = async () => await client.GetCapabilitiesAsync(CancellationToken.None);

        await firstAttempt.Should().ThrowAsync<GenProxy.Api.Integrations.Contracts.LlamaRuntimeCallException>();

        var secondResult = await client.GetCapabilitiesAsync(CancellationToken.None);

        secondResult.ModelId.Should().Be("runtime-model");
        transport.GetCapabilitiesCallCount.Should().Be(2);
    }

    [Fact]
    public async Task GetCapabilitiesAsync_WhenCancelled_PropagatesCallerCancellationToTransport()
    {
        var transport = new FakeGrpcTransport();
        transport.CapabilityResponses.Enqueue(async cancellationToken =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new GetCapabilitiesReply();
        });
        var client = new GrpcLlamaRuntimeClient(RuntimeName, NullLogger<GrpcLlamaRuntimeClient>.Instance, transport);
        using var cancellationTokenSource = new CancellationTokenSource();

        var capabilitiesTask = client.GetCapabilitiesAsync(cancellationTokenSource.Token);
        cancellationTokenSource.CancelAfter(TimeSpan.FromMilliseconds(100));

        var act = async () => await capabilitiesTask.WaitAsync(TimeSpan.FromSeconds(1));

        await act.Should().ThrowAsync<OperationCanceledException>();
        transport.GetCapabilitiesCallCount.Should().Be(1);
    }

    [Fact]
    public async Task EstimateTokensAsync_WhenTransportThrowsRpcException_ThrowsLlamaRuntimeCallException()
    {
        var transport = new FakeGrpcTransport
        {
            EstimateException = new RpcException(new Status(StatusCode.Unavailable, "offline"))
        };
        var client = new GrpcLlamaRuntimeClient(RuntimeName, NullLogger<GrpcLlamaRuntimeClient>.Instance, transport);

        var act = async () => await client.EstimateTokensAsync("prompt", CancellationToken.None);

        var exception = await act.Should().ThrowAsync<GenProxy.Api.Integrations.Contracts.LlamaRuntimeCallException>();
        exception.Which.Message.Should().Be("Failed to estimate tokens against the test runtime.");
        exception.Which.InnerException.Should().BeOfType<RpcException>();
    }

    [Fact]
    public async Task GenerateAsync_WhenPromptReducerReportsBudgetExceeded_ThrowsLlamaRuntimePromptBudgetExceededException()
    {
        var transport = new FakeGrpcTransport
        {
            GenerateException = new RpcException(new Status(StatusCode.InvalidArgument, "Prompt exceeds input budget: 6250 tokens > 3584 allowed."))
        };
        var client = new GrpcLlamaRuntimeClient("prompt_reducer", NullLogger<GrpcLlamaRuntimeClient>.Instance, transport);

        var act = async () => await client.GenerateAsync("req_123", "prompt", null, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<GenProxy.Api.Integrations.Contracts.LlamaRuntimePromptBudgetExceededException>();
        exception.Which.Message.Should().Contain("Prompt exceeds input budget");
        exception.Which.InnerException.Should().BeOfType<RpcException>();
    }

    [Fact]
    public async Task GenerateAsync_WhenNonBudgetInvalidArgument_ThrowsLlamaRuntimeCallException()
    {
        var transport = new FakeGrpcTransport
        {
            GenerateException = new RpcException(new Status(StatusCode.InvalidArgument, "schema mismatch"))
        };
        var client = new GrpcLlamaRuntimeClient("prompt_reducer", NullLogger<GrpcLlamaRuntimeClient>.Instance, transport);

        var act = async () => await client.GenerateAsync("req_123", "prompt", null, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<GenProxy.Api.Integrations.Contracts.LlamaRuntimeCallException>();
        exception.Which.Message.Should().Be("Failed to generate against the prompt_reducer runtime.");
        exception.Which.InnerException.Should().BeOfType<RpcException>();
    }

    [Fact]
    public async Task GenerateAsync_WhenRuntimeRejectsGenerationOverrides_ThrowsLlamaRuntimeUnsupportedGenerationOverridesException()
    {
        var trailers = new Metadata
        {
            { "runtime-error-code", "unsupported_generation_overrides" }
        };
        var transport = new FakeGrpcTransport
        {
            GenerateException = new RpcException(
                new Status(StatusCode.InvalidArgument, "Request-level generation overrides are not supported by this runtime yet. Omit Generation to use runtime defaults."),
                trailers)
        };
        var client = new GrpcLlamaRuntimeClient(RuntimeName, NullLogger<GrpcLlamaRuntimeClient>.Instance, transport);

        var act = async () => await client.GenerateAsync("req_123", "prompt", null, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<GenProxy.Api.Integrations.Contracts.LlamaRuntimeUnsupportedGenerationOverridesException>();
        exception.Which.Message.Should().Contain("Request-level generation overrides are not supported");
        exception.Which.InnerException.Should().BeOfType<RpcException>();
    }

    [Fact]
    public async Task GenerateAsync_WhenRuntimeReportsInvalidArgument_ThrowsLlamaRuntimeInvalidArgumentException()
    {
        var trailers = new Metadata
        {
            { "runtime-error-code", "invalid_argument" }
        };
        var transport = new FakeGrpcTransport
        {
            GenerateException = new RpcException(new Status(StatusCode.InvalidArgument, "ResponseFormat.JsonSchema must be valid JSON."), trailers)
        };
        var client = new GrpcLlamaRuntimeClient(RuntimeName, NullLogger<GrpcLlamaRuntimeClient>.Instance, transport);

        var act = async () => await client.GenerateAsync("req_123", "prompt", null, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<GenProxy.Api.Integrations.Contracts.LlamaRuntimeInvalidArgumentException>();
        exception.Which.Message.Should().Contain("ResponseFormat.JsonSchema");
        exception.Which.InnerException.Should().BeOfType<RpcException>();
    }

    [Fact]
    public async Task GenerateAsync_WhenRuntimeReportsLegacyStructuredOutputFailure_ThrowsLlamaRuntimeStructuredOutputNotSatisfiedException()
    {
        var transport = new FakeGrpcTransport
        {
            GenerateException = new RpcException(new Status(StatusCode.Internal, "Inference did not return a valid JSON object."))
        };
        var client = new GrpcLlamaRuntimeClient(RuntimeName, NullLogger<GrpcLlamaRuntimeClient>.Instance, transport);

        var act = async () => await client.GenerateAsync("req_123", "prompt", null, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<GenProxy.Api.Integrations.Contracts.LlamaRuntimeStructuredOutputNotSatisfiedException>();
        exception.Which.Message.Should().Be("Inference did not return a valid JSON object.");
        exception.Which.InnerException.Should().BeOfType<RpcException>();
    }

    [Theory]
    [InlineData("Generated output did not match the requested JSON schema.")]
    [InlineData("Structured output generation returned empty content.")]
    [InlineData("")]
    public async Task GenerateAsync_WhenRuntimeReportsTypedStructuredOutputFailure_ThrowsLlamaRuntimeStructuredOutputNotSatisfiedException(string detail)
    {
        var rpcException = new RpcException(new Status(StatusCode.Internal, detail), new Metadata
        {
            { "runtime-error-code", "structured_output_failed" }
        });
        var transport = new FakeGrpcTransport { GenerateException = rpcException };
        var client = new GrpcLlamaRuntimeClient(RuntimeName, NullLogger<GrpcLlamaRuntimeClient>.Instance, transport);

        var act = async () => await client.GenerateAsync("req_123", "prompt", null, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<GenProxy.Api.Integrations.Contracts.LlamaRuntimeStructuredOutputNotSatisfiedException>();
        exception.Which.Message.Should().Be(detail);
        exception.Which.InnerException.Should().BeSameAs(rpcException);
    }

    [Theory]
    [InlineData(StatusCode.Unavailable, "structured_output_failed", "Inference did not return a valid JSON object.")]
    [InlineData(StatusCode.InvalidArgument, "structured_output_failed", "Generated output did not match the requested JSON schema.")]
    [InlineData(StatusCode.Internal, "unknown_error", "Generated output did not match the requested JSON schema.")]
    [InlineData(StatusCode.Internal, null, "Generated output did not match the requested JSON schema.")]
    public async Task GenerateAsync_WhenStructuredOutputFailureIsNotRecognized_ThrowsLlamaRuntimeCallException(StatusCode statusCode, string? errorCode, string detail)
    {
        var trailers = new Metadata();
        if (errorCode is not null)
        {
            trailers.Add("runtime-error-code", errorCode);
        }

        var rpcException = new RpcException(new Status(statusCode, detail), trailers);
        var transport = new FakeGrpcTransport { GenerateException = rpcException };
        var client = new GrpcLlamaRuntimeClient(RuntimeName, NullLogger<GrpcLlamaRuntimeClient>.Instance, transport);

        var act = async () => await client.GenerateAsync("req_123", "prompt", null, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<GenProxy.Api.Integrations.Contracts.LlamaRuntimeCallException>();
        exception.Which.InnerException.Should().BeSameAs(rpcException);
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

        var act = async () => await client.GenerateAsync("req_123", "prompt", null, CancellationToken.None);

        await act.Should().ThrowAsync<GenProxy.Api.Integrations.Contracts.LlamaRuntimePromptBudgetExceededException>();
        logger.Entries.Should().ContainSingle();
        logger.Entries[0].Level.Should().Be(LogLevel.Information);
        logger.Entries[0].Exception.Should().BeNull();
        logger.Entries[0].Message.Should().Contain("Upstream runtime rejected prompt due to token budget.");
    }

    [Fact]
    public async Task GenerateAsync_WhenTransportThrowsHttpRequestException_ThrowsLlamaRuntimeCallException()
    {
        var transport = new FakeGrpcTransport
        {
            GenerateException = new HttpRequestException("offline")
        };
        var client = new GrpcLlamaRuntimeClient(RuntimeName, NullLogger<GrpcLlamaRuntimeClient>.Instance, transport);

        var act = async () => await client.GenerateAsync("req_123", "prompt", null, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<GenProxy.Api.Integrations.Contracts.LlamaRuntimeCallException>();
        exception.Which.Message.Should().Be("Failed to generate against the test runtime.");
        exception.Which.InnerException.Should().BeOfType<HttpRequestException>();
    }

    [Theory]
    [MemberData(nameof(RuntimeOperationOutcomes))]
    public async Task RuntimeOperation_PreservesErrorAndMetricSemantics(string operation, string outcome)
    {
        var runtimeName = $"metrics-{Guid.NewGuid():N}";
        using var cancellation = new CancellationTokenSource();
        Exception? original = outcome switch
        {
            "success" => null,
            "http_failure" => new HttpRequestException("offline"),
            "timeout" or "canceled" => new RpcException(new Status(StatusCode.DeadlineExceeded, "deadline expired")),
            _ => new RpcException(new Status(StatusCode.Unavailable, "offline"))
        };
        if (outcome == "canceled")
        {
            cancellation.Cancel();
        }

        var transport = new FakeGrpcTransport
        {
            EstimateException = original,
            CapabilitiesException = original,
            GenerateException = original
        };
        var client = new GrpcLlamaRuntimeClient(runtimeName, NullLogger<GrpcLlamaRuntimeClient>.Instance, transport);
        var failures = 0L;
        var durations = new List<double>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "GenProxy.Api")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            if (instrument.Name == "genproxy.upstream.failures" &&
                tags.ToArray().Any(tag => tag.Key == "runtime" && Equals(tag.Value, runtimeName)))
            {
                failures += value;
            }
        });
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            if (instrument.Name == "genproxy.upstream.duration_ms" &&
                tags.ToArray().Any(tag => tag.Key == "runtime" && Equals(tag.Value, runtimeName)))
            {
                tags.ToArray().Should().Contain(tag => tag.Key == "operation" && Equals(tag.Value, operation));
                durations.Add(value);
            }
        });
        listener.Start();
        Func<Task> call = () => operation switch
        {
            "estimate_tokens" => client.EstimateTokensAsync("prompt", cancellation.Token),
            "get_capabilities" => client.GetCapabilitiesAsync(cancellation.Token),
            _ => client.GenerateAsync("req_123", "prompt", null, cancellation.Token)
        };

        if (outcome == "success")
        {
            await call();
            durations.Should().ContainSingle().Which.Should().BeGreaterThanOrEqualTo(0);
        }
        else
        {
            var failure = await call.Should().ThrowAsync<Exception>();
            failure.Which.InnerException.Should().BeSameAs(original);
            if (outcome == "canceled")
            {
                failure.Which.Should().BeOfType<OperationCanceledException>()
                    .Which.CancellationToken.Should().Be(cancellation.Token);
            }
            else if (outcome == "timeout")
            {
                failure.Which.Should().BeOfType<LlamaRuntimeTimeoutException>();
            }
            else
            {
                failure.Which.Should().BeOfType<LlamaRuntimeCallException>();
                failure.Which.Message.Should().Be($"Failed to {operation.Replace('_', ' ')} against the {runtimeName} runtime.");
            }
            durations.Should().BeEmpty();
        }
        failures.Should().Be(outcome is "success" or "canceled" ? 0 : 1);
    }

    public static IEnumerable<object[]> RuntimeOperationOutcomes() =>
        from operation in new[] { "estimate_tokens", "get_capabilities", "generate" }
        from outcome in new[] { "success", "rpc_failure", "http_failure", "timeout", "canceled" }
        select new object[] { operation, outcome };

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
    public void PromptReducerRuntimeOptions_BindsEnabledAddressAndApiKeyFromConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{PromptReducerRuntimeOptions.SectionName}:Enabled"] = "false",
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

        options.Enabled.Should().BeFalse();
        options.Address.Should().Be("https://reducer.test");
        options.ApiKey.Should().Be("reducer-key");
    }

    [Fact]
    public void PromptReductionOptions_BindsSummarizationPromptTemplateFromConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{PromptReductionOptions.SectionName}:SummarizationPromptTemplate"] = "condense {{prompt}} to {{max_tokens}}"
            })
            .Build();

        var services = new ServiceCollection();
        services.Configure<PromptReductionOptions>(configuration.GetSection(PromptReductionOptions.SectionName));

        using var serviceProvider = services.BuildServiceProvider();
        var options = serviceProvider.GetRequiredService<IOptions<PromptReductionOptions>>().Value;

        options.SummarizationPromptTemplate.Should().Be("condense {{prompt}} to {{max_tokens}}");
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
        private readonly object _capabilityResponsesSync = new();
        private int _getCapabilitiesCallCount;

        public EstimateTokensReply? EstimateReply { get; init; }

        public GetCapabilitiesReply? CapabilitiesReply { get; init; }

        public GenerateReply? GenerateReply { get; init; }

        public Exception? EstimateException { get; init; }

        public Exception? CapabilitiesException { get; init; }

        public Exception? GenerateException { get; init; }

        public EstimateTokensRequest? LastEstimateRequest { get; private set; }

        public int GetCapabilitiesCallCount => _getCapabilitiesCallCount;

        public GenerateRequest? LastGenerateRequest { get; private set; }

        public Queue<Func<CancellationToken, Task<GetCapabilitiesReply>>> CapabilityResponses { get; } = new();

        public Task<EstimateTokensReply> EstimateTokensAsync(EstimateTokensRequest request, CancellationToken cancellationToken)
        {
            LastEstimateRequest = request;

            return EstimateException is not null
                ? Task.FromException<EstimateTokensReply>(EstimateException)
                : Task.FromResult(EstimateReply ?? new EstimateTokensReply());
        }

        public Task<GetCapabilitiesReply> GetCapabilitiesAsync(GetCapabilitiesRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _getCapabilitiesCallCount);

            Func<CancellationToken, Task<GetCapabilitiesReply>>? responseFactory;
            lock (_capabilityResponsesSync)
            {
                responseFactory = CapabilityResponses.Count > 0
                    ? CapabilityResponses.Dequeue()
                    : null;
            }

            if (responseFactory is not null)
            {
                return responseFactory(cancellationToken);
            }

            return CapabilitiesException is not null
                ? Task.FromException<GetCapabilitiesReply>(CapabilitiesException)
                : Task.FromResult(CapabilitiesReply ?? new GetCapabilitiesReply());
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
