using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using GenProxy.Api.Integrations.Contracts;
using GenProxy.Api.Integrations.Contracts.Models;
using GenProxy.Api.Services.Contracts;
using GenProxy.Api.Services.Contracts.Models;
using GenProxy.Api.Services.Implementation.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GenProxy.Api.IntegrationTests;

public class ResponsesReductionCancellationTests
{
    [Fact]
    public async Task PostResponses_WhenClientDisconnectsDuringReduction_CancelsWithoutFallbackGenerationOrBadGatewayReport()
    {
        var state = new ReductionState();
        await using var factory = CreateFactory(state, TimeSpan.FromSeconds(20));
        using var client = CreateClient(factory);
        using var cancellation = new CancellationTokenSource();
        var pending = client.PostAsJsonAsync("/v1/responses", RequestBody(), cancellation.Token);
        await state.Reducer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();

        Func<Task> observe = () => pending.WaitAsync(TimeSpan.FromSeconds(5));
        await observe.Should().ThrowAsync<OperationCanceledException>();
        var failure = await state.PipelineFailure.Task.WaitAsync(TimeSpan.FromSeconds(5));
        failure.Should().BeOfType<TaskCanceledException>();
        failure.Should().BeSameAs(state.Reducer.Failure);
        ((OperationCanceledException)failure).CancellationToken.Should().Be(state.Reducer.Token);
        await AssertNoContinuationOrBadGatewayAsync(state);
    }

    [Fact]
    public async Task PostResponses_WhenOverallTimeoutExpiresDuringReduction_ReturnsGatewayTimeoutWithoutFallbackOrGeneration()
    {
        var state = new ReductionState();
        await using var factory = CreateFactory(state, TimeSpan.FromSeconds(1));
        using var client = CreateClient(factory);

        using var response = await client.PostAsJsonAsync("/v1/responses", RequestBody());

        response.StatusCode.Should().Be(HttpStatusCode.GatewayTimeout);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Title.Should().Be("Response request timed out.");
        problem.Status.Should().Be(504);
        var failure = await state.PipelineFailure.Task.WaitAsync(TimeSpan.FromSeconds(5));
        failure.Should().BeAssignableTo<OperationCanceledException>();
        failure.Should().BeSameAs(state.Reducer.Failure);
        await AssertNoContinuationOrBadGatewayAsync(state);
    }

    [Fact]
    public async Task PostResponses_WhenReducerRuntimeDeadlineExpires_ReturnsUpstreamGatewayTimeoutWithoutFallbackOrGeneration()
    {
        var state = new ReductionState(runtimeTimeout: true);
        await using var factory = CreateFactory(state, TimeSpan.FromSeconds(20));
        using var client = CreateClient(factory);

        using var response = await client.PostAsJsonAsync("/v1/responses", RequestBody());

        response.StatusCode.Should().Be(HttpStatusCode.GatewayTimeout);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Title.Should().Be("Upstream runtime timed out.");
        problem.Status.Should().Be(504);
        var failure = await state.PipelineFailure.Task.WaitAsync(TimeSpan.FromSeconds(5));
        failure.Should().BeOfType<LlamaRuntimeTimeoutException>();
        failure.Should().BeSameAs(state.Reducer.Failure);
        await AssertNoContinuationOrBadGatewayAsync(state);
    }

    private static async Task AssertNoContinuationOrBadGatewayAsync(ReductionState state)
    {
        var status = await state.RequestFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        status.Should().NotBe(502);
        state.Fallback.CallCount.Should().Be(0);
        state.Generation.EstimationCount.Should().Be(1);
        state.Generation.GenerationCount.Should().Be(0);
        state.Logs.Entries.Should().NotContain(entry => entry.Exception is PromptReductionFailedException);
        state.Logs.Entries.Should().NotContain(entry => entry.Message.Contains("Prompt reduction failed.", StringComparison.Ordinal));
    }

    private static WebApplicationFactory<Program> CreateFactory(ReductionState state, TimeSpan timeout)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("PromptReducerRuntime:Enabled", "true");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ApiKeys:Keys:0"] = "reduction-test-key",
                ["PromptReducerRuntime:Enabled"] = "true",
                ["PromptReducerRuntime:Address"] = "https://localhost:50052",
                ["ResponsesTimeout:Timeout"] = timeout.ToString("c")
            }));
            builder.ConfigureLogging(logging => logging.AddProvider(state.Logs));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IGenerationRuntimeClient>();
                services.AddSingleton<IGenerationRuntimeClient>(state.Generation);
                services.RemoveAll<IPromptReducerRuntimeClient>();
                services.AddSingleton<IPromptReducerRuntimeClient>(state.Reducer);
                services.RemoveAll<IPromptReducer>();
                services.AddSingleton<IPromptReducer, LlmPromptSummarizer>();
                services.AddSingleton<IPromptReducer>(state.Fallback);
                services.RemoveAll<IPromptReductionPipeline>();
                services.AddSingleton<IPromptReductionPipeline>(provider => new ObservedPipeline(
                    new PromptReductionPipeline(provider.GetServices<IPromptReducer>()), state));
                services.AddSingleton<IStartupFilter>(new RequestCompletionFilter(state));
            });
        });
        // A socket-backed host makes HttpClient cancellation exercise a real client disconnect.
        factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        return factory;
    }

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(10);
        client.DefaultRequestHeaders.Add("X-API-Key", "reduction-test-key");
        return client;
    }

    private static object RequestBody() => new
    {
        model = "test-model",
        input = new[] { new { type = "message", role = "user", content = new[] { new { type = "input_text", text = "long prompt" } } } }
    };

    private sealed class ReductionState(bool runtimeTimeout = false)
    {
        public BlockingReducerRuntime Reducer { get; } = new(runtimeTimeout);
        public RecordingGenerationRuntime Generation { get; } = new();
        public RecordingFallback Fallback { get; } = new();
        public RecordingLogProvider Logs { get; } = new();
        public TaskCompletionSource<Exception> PipelineFailure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<int> RequestFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ObservedPipeline(PromptReductionPipeline pipeline, ReductionState state) : IPromptReductionPipeline
    {
        public async Task<PromptReductionResult> ReduceAsync(string prompt, int maxAllowedInputTokens, CancellationToken cancellationToken)
        {
            try
            {
                return await pipeline.ReduceAsync(prompt, maxAllowedInputTokens, cancellationToken);
            }
            catch (Exception exception)
            {
                state.PipelineFailure.TrySetResult(exception);
                throw;
            }
        }
    }

    private sealed class RequestCompletionFilter(ReductionState state) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                try
                {
                    await nextMiddleware();
                }
                finally
                {
                    if (context.Request.Path == "/v1/responses")
                    {
                        state.RequestFinished.TrySetResult(context.Response.StatusCode);
                    }
                }
            });
            next(app);
        };
    }

    private sealed class BlockingReducerRuntime(bool runtimeTimeout) : IPromptReducerRuntimeClient
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token { get; private set; }
        public Exception? Failure { get; private set; }

        public Task<TokenEstimation> EstimateTokensAsync(string prompt, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<LlamaCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public async Task<LlamaGenerationResult> GenerateAsync(string requestId, string prompt, LlamaGenerationOptions? options, CancellationToken cancellationToken)
        {
            Token = cancellationToken;
            Started.TrySetResult();
            try
            {
                if (runtimeTimeout)
                {
                    throw new LlamaRuntimeTimeoutException("Prompt reducer deadline expired.");
                }

                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("The blocking reducer cannot complete without cancellation.");
            }
            catch (Exception exception)
            {
                Failure = exception;
                throw;
            }
        }
    }

    private sealed class RecordingGenerationRuntime : IGenerationRuntimeClient
    {
        public int EstimationCount { get; private set; }
        public int GenerationCount { get; private set; }

        public Task<TokenEstimation> EstimateTokensAsync(string prompt, CancellationToken cancellationToken)
        {
            EstimationCount++;
            return Task.FromResult(new TokenEstimation(100, 100, 10, 90, EstimationCount > 1));
        }

        public Task<LlamaCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<LlamaGenerationResult> GenerateAsync(string requestId, string prompt, LlamaGenerationOptions? options, CancellationToken cancellationToken)
        {
            GenerationCount++;
            return Task.FromResult(new LlamaGenerationResult(requestId, "test-model", "generated", null, null));
        }
    }

    private sealed class RecordingFallback : IPromptReducer
    {
        public int Order => 200;
        public int CallCount { get; private set; }

        public Task<PromptReductionResult> ReduceAsync(string prompt, int maxAllowedInputTokens, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new PromptReductionResult("short", true, PromptReductionStrategy.LeadingTruncation));
        }
    }

    private sealed record LogEntry(string Message, Exception? Exception);

    private sealed class RecordingLogProvider : ILoggerProvider
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) => new RecordingLogger(Entries);
        public void Dispose() { }
    }

    private sealed class RecordingLogger(ConcurrentQueue<LogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullLogger.Instance.BeginScope(state);
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            entries.Enqueue(new LogEntry(formatter(state, exception), exception));
    }
}
