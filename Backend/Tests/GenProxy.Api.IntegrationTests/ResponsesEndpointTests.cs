using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using GenProxy.Api.Host;
using GenProxy.Api.Host.Endpoints;
using GenProxy.Api.Integrations.Contracts;
using GenProxy.Api.Integrations.Contracts.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using GenProxy.Api.Host.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace GenProxy.Api.IntegrationTests;

public class ResponsesEndpointTests
{
    [Fact]
    public async Task PostResponses_WithValidApiKey_ReturnsResponsesCompatibleResponse()
    {
        await using var factory = CreateFactory(
            new FakeGenerationRuntimeClient(),
            new FakePromptReducerRuntimeClient());

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(Constants.Auth.ApiKeyHeaderName, "test-api-key");

        var response = await client.PostAsJsonAsync("/v1/responses", new
        {
            model = "gpt-5.1",
            input = "hello world",
            max_output_tokens = 64
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<ResponsesApiResponse>();
        payload.Should().NotBeNull();
        payload!.Object.Should().Be("response");
        payload.Model.Should().Be("gpt-5.1");
        payload.OutputText.Should().Be("generated: hello world");
        payload.Usage.OutputTokens.Should().BeNull();
        payload.Usage.TotalTokens.Should().BeNull();
        payload.CreatedAt.Should().BePositive();
    }

    [Fact]
    public async Task PostResponses_WithoutApiKey_ReturnsUnauthorized()
    {
        await using var factory = CreateFactory(
            new FakeGenerationRuntimeClient(),
            new FakePromptReducerRuntimeClient());

        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/v1/responses", new
        {
            model = "gpt-5.1",
            input = "hello world"
        });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PostResponses_WhenPromptStillTooLarge_ReturnsUnprocessableEntity()
    {
        await using var factory = CreateFactory(
            new FakeGenerationRuntimeClient(fitsAfterReduction: false, oversizedPrompt: "hello world"),
            new FakePromptReducerRuntimeClient());

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(Constants.Auth.ApiKeyHeaderName, "test-api-key");

        var response = await client.PostAsJsonAsync("/v1/responses", new
        {
            model = "gpt-5.1",
            input = "hello world"
        });

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        problem.Should().NotBeNull();
        problem!.Title.Should().Be("Prompt exceeds token budget.");
        problem.Detail.Should().Contain("after prompt reduction");
    }

    [Fact]
    public async Task PostResponses_WhenRequestInvalid_ReturnsValidationProblem()
    {
        await using var factory = CreateFactory(
            new FakeGenerationRuntimeClient(),
            new FakePromptReducerRuntimeClient());

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(Constants.Auth.ApiKeyHeaderName, "test-api-key");

        var response = await client.PostAsJsonAsync("/v1/responses", new
        {
            model = "",
            input = ""
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostResponses_WhenInputTooLarge_ReturnsValidationProblem()
    {
        await using var factory = CreateFactory(
            new FakeGenerationRuntimeClient(),
            new FakePromptReducerRuntimeClient());

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(Constants.Auth.ApiKeyHeaderName, "test-api-key");

        var response = await client.PostAsJsonAsync("/v1/responses", new
        {
            model = "gpt-5.1",
            input = new string('a', 65537)
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        problem.Should().NotBeNull();
        problem!.Errors.Should().ContainKey("input");
    }

    [Fact]
    public async Task PostResponses_WhenMetadataHasTooManyEntries_ReturnsValidationProblem()
    {
        await using var factory = CreateFactory(
            new FakeGenerationRuntimeClient(),
            new FakePromptReducerRuntimeClient());

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(Constants.Auth.ApiKeyHeaderName, "test-api-key");

        var metadata = Enumerable.Range(0, 17)
            .ToDictionary(index => $"key{index}", index => "value");

        var response = await client.PostAsJsonAsync("/v1/responses", new
        {
            model = "gpt-5.1",
            input = "hello world",
            metadata
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        problem.Should().NotBeNull();
        problem!.Errors.Should().ContainKey("metadata");
    }

    [Fact]
    public async Task PostResponses_WhenRequestBodyTooLarge_ReturnsPayloadTooLarge()
    {
        await using var factory = CreateFactory(
            new FakeGenerationRuntimeClient(),
            new FakePromptReducerRuntimeClient());

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = JsonContent.Create(new
            {
                model = "gpt-5.1",
                input = new string('a', 140000)
            })
        };
        request.Headers.Add(Constants.Auth.ApiKeyHeaderName, "test-api-key");

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task PostResponses_WhenReducerRuntimeDisabled_UsesLeadingTruncationWithoutReducerClient()
    {
        await using var factory = CreateFactory(
            new FakeGenerationRuntimeClient(truncatedPrompt: "lloworld", oversizedPrompt: "helloworld"),
            promptReducerRuntimeClient: null,
            usePromptReducerRuntime: false);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(Constants.Auth.ApiKeyHeaderName, "test-api-key");

        var response = await client.PostAsJsonAsync("/v1/responses", new
        {
            model = "gpt-5.1",
            input = "helloworld"
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<ResponsesApiResponse>();
        payload.Should().NotBeNull();
        payload!.OutputText.Should().Be("generated: lloworld");
    }

    [Fact]
    public async Task PostResponses_WhenGenerationRuntimeUnavailable_ReturnsServiceUnavailableProblem()
    {
        await using var factory = CreateFactory(
            new ThrowingGenerationRuntimeClient(new UpstreamRuntimeException("generation runtime offline")),
            new FakePromptReducerRuntimeClient());

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(Constants.Auth.ApiKeyHeaderName, "test-api-key");

        var response = await client.PostAsJsonAsync("/v1/responses", new
        {
            model = "gpt-5.1",
            input = "hello world"
        });

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        problem.Should().NotBeNull();
        problem!.Title.Should().Be("Upstream runtime unavailable.");
    }

    [Fact]
    public async Task PostResponses_WhenReducerRuntimeRejectsOversizedReductionPrompt_ReturnsUnprocessableEntity()
    {
        await using var factory = CreateFactory(
            new FakeGenerationRuntimeClient(fitsAfterReduction: false, oversizedPrompt: "hello world"),
            new ThrowingPromptReducerRuntimeClient(
                new UpstreamPromptBudgetExceededException("Prompt exceeds input budget: 6250 tokens > 3584 allowed.")));

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(Constants.Auth.ApiKeyHeaderName, "test-api-key");

        var response = await client.PostAsJsonAsync("/v1/responses", new
        {
            model = "gpt-5.1",
            input = "hello world"
        });

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        problem.Should().NotBeNull();
        problem!.Title.Should().Be("Prompt exceeds token budget.");
        problem.Detail.Should().Contain("Prompt exceeds input budget");
    }

    [Fact]
    public async Task PostResponses_WithInvalidGenerationRuntimeAddress_FailsStartupValidation()
    {
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.ConfigureAppConfiguration((_, configBuilder) =>
                {
                    configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["GenerationRuntime:Address"] = "not-a-valid-uri"
                    });
                });
            });

        var act = () => factory.CreateClient();

        act.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void PostResponses_WithPlaintextGenerationRuntimeAddress_FailsStartupValidation()
    {
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.ConfigureAppConfiguration((_, configBuilder) =>
                {
                    configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["GenerationRuntime:Address"] = "http://localhost:50051"
                    });
                });
            });

        var act = () => factory.CreateClient();

        act.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void PostResponses_WithoutConfiguredApiKeys_FailsStartupValidation()
    {
        using var factory = CreateFactory(
            new FakeGenerationRuntimeClient(),
            new FakePromptReducerRuntimeClient(),
            configureServices: services =>
            {
                services.PostConfigure<ApiKeyOptions>(options => options.Keys.Clear());
            });

        var act = () => factory.CreateClient();

        act.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public async Task PostResponses_WithoutConfiguredApiKeys_AllowsRequestsOnlyWhenDevelopmentOptOutEnabled()
    {
        await using var factory = CreateFactory(
            new FakeGenerationRuntimeClient(),
            new FakePromptReducerRuntimeClient(),
            environmentName: Environments.Development,
            configureServices: services =>
            {
                services.PostConfigure<ApiKeyOptions>(options =>
                {
                    options.Keys.Clear();
                    options.AllowUnauthenticatedInDevelopment = true;
                });
            });

        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/v1/responses", new
        {
            model = "gpt-5.1",
            input = "hello world"
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PostResponses_EmitsCombinedHttpLogWithBodiesAndRedactsApiKeyValue()
    {
        var sink = new LogSink();

        await using var factory = CreateFactory(
            new FakeGenerationRuntimeClient(),
            new FakePromptReducerRuntimeClient(),
            configureLogging: logging =>
            {
                logging.ClearProviders();
                logging.AddProvider(new SinkLoggerProvider(sink));
                logging.SetMinimumLevel(LogLevel.Information);
            },
            configureSettings: settings =>
            {
                settings["Logging:LogLevel:Default"] = "Information";
                settings["Logging:LogLevel:Microsoft.AspNetCore"] = "Warning";
                settings["Logging:LogLevel:Microsoft.AspNetCore.HttpLogging.HttpLoggingMiddleware"] = "Information";
            });

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(Constants.Auth.ApiKeyHeaderName, "test-api-key");

        var response = await client.PostAsJsonAsync("/v1/responses", new
        {
            model = "gpt-5.1",
            input = "hello world"
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var httpLogs = sink.Entries
            .Where(entry => entry.Category == "Microsoft.AspNetCore.HttpLogging.HttpLoggingMiddleware")
            .ToList();

        httpLogs.Should().ContainSingle();
        httpLogs[0].Message.Should().Contain("\"input\":\"hello world\"");
        httpLogs[0].Message.Should().Contain("\"output_text\":\"generated: hello world\"");
        httpLogs[0].Message.Should().NotContain("test-api-key");
    }

    private static WebApplicationFactory<Program> CreateFactory(
        IGenerationRuntimeClient generationRuntimeClient,
        IPromptReducerRuntimeClient? promptReducerRuntimeClient,
        bool usePromptReducerRuntime = true,
        string environmentName = "Testing",
        Action<Dictionary<string, string?>>? configureSettings = null,
        Action<IServiceCollection>? configureServices = null,
        Action<ILoggingBuilder>? configureLogging = null)
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment(environmentName);
                builder.ConfigureAppConfiguration((_, configBuilder) =>
                {
                    var settings = new Dictionary<string, string?>
                    {
                        ["PromptReduction:UsePromptReducerRuntime"] = usePromptReducerRuntime.ToString(),
                        ["GenerationRuntime:Address"] = "https://localhost:50051",
                        ["PromptReducerRuntime:Address"] = "https://localhost:50052"
                    };
                    configureSettings?.Invoke(settings);
                    configBuilder.AddInMemoryCollection(settings);
                });
                builder.ConfigureLogging(logging => configureLogging?.Invoke(logging));
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IGenerationRuntimeClient>();
                    services.AddSingleton(generationRuntimeClient);
                    if (promptReducerRuntimeClient is not null)
                    {
                        services.RemoveAll<IPromptReducerRuntimeClient>();
                        services.AddSingleton(promptReducerRuntimeClient);
                    }

                    configureServices?.Invoke(services);
                });
            });
    }

    private sealed class FakeGenerationRuntimeClient(
        bool fitsAfterReduction = true,
        string truncatedPrompt = "__none__",
        string oversizedPrompt = "__oversized__") : IGenerationRuntimeClient
    {
        private readonly string _truncatedPrompt = truncatedPrompt;
        private readonly string _oversizedPrompt = oversizedPrompt;

        public Task<TokenEstimation> EstimateTokensAsync(string prompt, CancellationToken cancellationToken)
        {
            if (prompt == _oversizedPrompt)
            {
                return Task.FromResult(new TokenEstimation(9000, 8192, 512, 7680, false));
            }

            if (prompt == _truncatedPrompt)
            {
                return Task.FromResult(new TokenEstimation(6000, 8192, 512, 7680, true));
            }

            return Task.FromResult(new TokenEstimation(6000, 8192, 512, 7680, fitsAfterReduction));
        }

        public Task<LlamaGenerationResult> GenerateAsync(string requestId, string prompt, CancellationToken cancellationToken)
        {
            return Task.FromResult(new LlamaGenerationResult(requestId, $"generated: {prompt}"));
        }
    }

    private sealed class FakePromptReducerRuntimeClient : IPromptReducerRuntimeClient
    {
        public Task<TokenEstimation> EstimateTokensAsync(string prompt, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<LlamaGenerationResult> GenerateAsync(string requestId, string prompt, CancellationToken cancellationToken)
        {
            var promptMarker = "Original user input:\n";
            var originalPrompt = prompt.Contains(promptMarker, StringComparison.Ordinal)
                ? prompt[(prompt.IndexOf(promptMarker, StringComparison.Ordinal) + promptMarker.Length)..]
                : prompt;

            return Task.FromResult(new LlamaGenerationResult(requestId, originalPrompt));
        }
    }

    private sealed class ThrowingGenerationRuntimeClient(Exception exception) : IGenerationRuntimeClient
    {
        private readonly Exception _exception = exception;

        public Task<TokenEstimation> EstimateTokensAsync(string prompt, CancellationToken cancellationToken)
        {
            throw _exception;
        }

        public Task<LlamaGenerationResult> GenerateAsync(string requestId, string prompt, CancellationToken cancellationToken)
        {
            throw _exception;
        }
    }

    private sealed class ThrowingPromptReducerRuntimeClient(Exception exception) : IPromptReducerRuntimeClient
    {
        private readonly Exception _exception = exception;

        public Task<TokenEstimation> EstimateTokensAsync(string prompt, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<LlamaGenerationResult> GenerateAsync(string requestId, string prompt, CancellationToken cancellationToken)
        {
            throw _exception;
        }
    }

    private sealed class LogSink
    {
        public List<LogEntry> Entries { get; } = [];
    }

    private sealed record LogEntry(string Category, LogLevel Level, string Message);

    private sealed class SinkLoggerProvider(LogSink sink) : ILoggerProvider
    {
        private readonly LogSink _sink = sink;

        public ILogger CreateLogger(string categoryName) => new SinkLogger(categoryName, _sink);

        public void Dispose()
        {
        }
    }

    private sealed class SinkLogger(string categoryName, LogSink sink) : ILogger
    {
        private readonly string _categoryName = categoryName;
        private readonly LogSink _sink = sink;

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _sink.Entries.Add(new LogEntry(_categoryName, logLevel, formatter(state, exception)));
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
