using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using GenProxy.Api.Integrations.Contracts;
using GenProxy.Api.Integrations.Contracts.Models;
using GenProxy.Api.Services.Contracts;
using GenProxy.Api.Services.Contracts.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace GenProxy.Api.IntegrationTests;

public class ResponsesPrivacyTests
{
    private const string PromptMarker = "PRIVATE_PROMPT_7ad26b";
    private const string OutputMarker = "PRIVATE_OUTPUT_e18c43";
    private const string KeyMarker = "PRIVATE_KEY_a9475e";

    [Theory]
    [InlineData("success", HttpStatusCode.OK)]
    [InlineData("invalid", HttpStatusCode.BadRequest)]
    [InlineData("unauthorized", HttpStatusCode.Unauthorized)]
    [InlineData("budget", HttpStatusCode.UnprocessableEntity)]
    [InlineData("unavailable", HttpStatusCode.ServiceUnavailable)]
    [InlineData("structured", HttpStatusCode.BadGateway)]
    public async Task DefaultLogs_ExcludePromptOutputAndCredentialsAcrossRequestOutcomes(string mode, HttpStatusCode expected)
    {
        var logs = new CapturedLogs();
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ApiKeys:Keys:0"] = KeyMarker,
                ["GenerationRuntime:Address"] = "https://localhost:50051",
                ["PromptReducerRuntime:Enabled"] = "false",
                ["Logging:LogLevel:Default"] = "Information",
                ["Logging:LogLevel:Microsoft.AspNetCore.HttpLogging.HttpLoggingMiddleware"] = "Information"
            }));
            builder.ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddProvider(logs);
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IGenerationRuntimeClient>();
                services.AddSingleton<IGenerationRuntimeClient>(new PrivacyRuntime(mode));
                services.RemoveAll<IPromptReductionPipeline>();
                services.AddSingleton<IPromptReductionPipeline, UnchangedReduction>();
            });
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        client.DefaultRequestHeaders.Add("x-api-key", mode == "unauthorized" ? KeyMarker + "_invalid" : KeyMarker);
        client.DefaultRequestHeaders.Add("Authorization", "Bearer " + KeyMarker);
        using var response = await client.PostAsJsonAsync("/v1/responses", new
        {
            model = "privacy-test",
            stream = mode == "invalid",
            response_format = mode == "structured" ? new { type = "json_schema", json_schema = new { name = "answer", schema = new { type = "object", properties = new { answer = new { type = "string" } }, required = new[] { "answer" }, additionalProperties = false }, strict = true } } : null,
            input = new[] { new { type = "message", role = "user", content = new[] { new { type = "input_text", text = PromptMarker } } } }
        });
        await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(expected);
        logs.Messages.Should().NotBeEmpty();
        var captured = string.Join('\n', logs.Messages);
        captured.Should().Contain("/v1/responses");
        captured.Should().NotContain(PromptMarker).And.NotContain(OutputMarker).And.NotContain(KeyMarker);
    }

    private sealed class PrivacyRuntime(string mode) : IGenerationRuntimeClient
    {
        public Task<LlamaCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new LlamaCapabilities("privacy-test", 100, true, true, false, "test"));

        public Task<TokenEstimation> EstimateTokensAsync(string prompt, CancellationToken cancellationToken) =>
            Task.FromResult(new TokenEstimation(2, 100, 10, 90, mode != "budget"));

        public Task<LlamaGenerationResult> GenerateAsync(string requestId, string prompt, LlamaGenerationOptions? options, CancellationToken cancellationToken)
        {
            if (mode == "unavailable")
            {
                throw new LlamaRuntimeCallException("Runtime unavailable.", new IOException("Runtime unavailable."));
            }

            return Task.FromResult(new LlamaGenerationResult(requestId, "privacy-test", OutputMarker, null, null));
        }
    }

    private sealed class UnchangedReduction : IPromptReductionPipeline
    {
        public Task<PromptReductionResult> ReduceAsync(string prompt, int maxAllowedInputTokens, CancellationToken cancellationToken) =>
            Task.FromResult(new PromptReductionResult(prompt, false, PromptReductionStrategy.None));
    }

    private sealed class CapturedLogs : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(Messages);
        public void Dispose() { }

        private sealed class CaptureLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                messages.Enqueue(formatter(state, exception) + exception?.ToString());
        }
    }
}
