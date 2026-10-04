using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using GenProxy.Api.Integrations.Contracts;
using GenProxy.Api.Integrations.Contracts.Configuration;
using GenProxy.Api.Integrations.Implementation.Clients;
using Google.Protobuf;
using Grpc.Net.Client;
using LlamaRuntime.Presentation.Grpc;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GenProxy.Api.IntegrationTests;

public class RuntimeTimeoutBoundaryTests
{
    [Theory]
    [InlineData("EstimateTokens")]
    [InlineData("GetCapabilities")]
    [InlineData("Generate")]
    public async Task RuntimeOperation_WhenHttpsUpstreamStalls_ExpiresItsDeadlineAndCancelsUpstream(string operation)
    {
        await using var runtime = await BoundaryRuntime.StartAsync(operation);
        var timeouts = OperationTimeouts(operation);
        var client = runtime.CreateClient(timeouts);
        var stopwatch = Stopwatch.StartNew();

        Func<Task> call = () => InvokeOperation(client, operation, CancellationToken.None);

        await call.Should().ThrowAsync<LlamaRuntimeTimeoutException>();
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        await runtime.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        runtime.Calls.Should().ContainSingle();
        runtime.Calls.Single().Deadline.Should().MatchRegex(@"^\d+[HMSmun]$");
        runtime.Calls.Single().ApiKey.Should().Be("boundary-runtime-key");
    }

    [Theory]
    [InlineData("EstimateTokens")]
    [InlineData("GetCapabilities")]
    [InlineData("Generate")]
    public async Task RuntimeOperation_WhenCallerCancels_PreservesCancellationAndCancelsHttpsUpstream(string operation)
    {
        await using var runtime = await BoundaryRuntime.StartAsync(operation);
        var client = runtime.CreateClient(LongTimeouts());
        using var cancellation = new CancellationTokenSource();
        var pending = InvokeOperation(client, operation, cancellation.Token);
        await runtime.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();

        Func<Task> observe = () => pending.WaitAsync(TimeSpan.FromSeconds(5));
        var exception = await observe.Should().ThrowAsync<OperationCanceledException>();
        exception.Which.CancellationToken.Should().Be(cancellation.Token);
        await runtime.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData("EstimateTokens")]
    [InlineData("GetCapabilities")]
    [InlineData("Generate")]
    public async Task PostResponses_WhenRuntimeOperationStalls_ReturnsGatewayTimeoutProblem(string operation)
    {
        await using var runtime = await BoundaryRuntime.StartAsync(operation);
        await using var factory = CreateFactory(runtime.CreateClient(OperationTimeouts(operation)), TimeSpan.FromSeconds(10));
        using var client = CreateHttpClient(factory);

        using var response = await client.PostAsJsonAsync("/v1/responses", RequestBody(operation == "GetCapabilities"));

        response.StatusCode.Should().Be(HttpStatusCode.GatewayTimeout);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Title.Should().Be("Upstream runtime timed out.");
        problem.Status.Should().Be(504);
        problem.Extensions.Should().ContainKey("trace_id");
        await runtime.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task PostResponses_WhenSequentialCallsExceedOverallTimeout_CancelsActiveUpstreamAndReturnsGatewayTimeout()
    {
        await using var runtime = await BoundaryRuntime.StartAsync("Generate", TimeSpan.FromMilliseconds(600));
        await using var factory = CreateFactory(runtime.CreateClient(LongTimeouts()), TimeSpan.FromSeconds(1));
        using var client = CreateHttpClient(factory);
        var stopwatch = Stopwatch.StartNew();

        using var response = await client.PostAsJsonAsync("/v1/responses", RequestBody(structured: true));

        response.StatusCode.Should().Be(HttpStatusCode.GatewayTimeout);
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Title.Should().Be("Response request timed out.");
        problem.Extensions.Should().ContainKey("trace_id");
        runtime.Calls.Select(call => call.Operation).Should().Equal("GetCapabilities", "EstimateTokens");
        await runtime.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task PostResponses_WhenHttpClientCancels_CancelsActiveUpstream()
    {
        await using var runtime = await BoundaryRuntime.StartAsync("EstimateTokens");
        await using var factory = CreateFactory(runtime.CreateClient(LongTimeouts()), TimeSpan.FromSeconds(20));
        using var client = CreateHttpClient(factory);
        using var cancellation = new CancellationTokenSource();
        var pending = client.PostAsJsonAsync("/v1/responses", RequestBody(), cancellation.Token);
        await runtime.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();

        Func<Task> observe = () => pending.WaitAsync(TimeSpan.FromSeconds(5));
        await observe.Should().ThrowAsync<OperationCanceledException>();
        await runtime.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task InvokeOperation(ILlamaRuntimeClient client, string operation, CancellationToken cancellationToken)
    {
        switch (operation)
        {
            case "EstimateTokens":
                await client.EstimateTokensAsync("prompt", cancellationToken);
                break;
            case "GetCapabilities":
                await client.GetCapabilitiesAsync(cancellationToken);
                break;
            case "Generate":
                await client.GenerateAsync("request-id", "prompt", null, cancellationToken);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }

    private static RuntimeTimeoutOptions LongTimeouts() => new()
    {
        EstimateTokens = TimeSpan.FromSeconds(20),
        GetCapabilities = TimeSpan.FromSeconds(20),
        Generate = TimeSpan.FromSeconds(20)
    };

    private static RuntimeTimeoutOptions OperationTimeouts(string operation)
    {
        var timeouts = LongTimeouts();
        switch (operation)
        {
            case "EstimateTokens": timeouts.EstimateTokens = TimeSpan.FromSeconds(1); break;
            case "GetCapabilities": timeouts.GetCapabilities = TimeSpan.FromSeconds(1); break;
            case "Generate": timeouts.Generate = TimeSpan.FromSeconds(1); break;
        }
        return timeouts;
    }

    private static object RequestBody(bool structured = false) => new
    {
        model = "test-model",
        input = new[] { new { type = "message", role = "user", content = new[] { new { type = "input_text", text = "hello" } } } },
        response_format = structured ? new
        {
            type = "json_schema",
            json_schema = new { name = "answer", schema = new { type = "object" }, strict = true }
        } : null
    };

    private static WebApplicationFactory<Program> CreateFactory(ILlamaRuntimeClient runtime, TimeSpan timeout) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("PromptReducerRuntime:Enabled", "false");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PromptReducerRuntime:Enabled"] = "false",
                ["ApiKeys:Keys:0"] = "boundary-public-key",
                ["ResponsesTimeout:Timeout"] = timeout.ToString("c")
            }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IGenerationRuntimeClient>();
                services.AddSingleton<IGenerationRuntimeClient>(new GenerationRuntimeClient(runtime));
            });
        });

    private static HttpClient CreateHttpClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        client.DefaultRequestHeaders.Add("X-API-Key", "boundary-public-key");
        client.Timeout = TimeSpan.FromSeconds(10);
        return client;
    }

    private sealed class BoundaryRuntime(WebApplication app, X509Certificate2 certificate, string stalledOperation, TimeSpan replyDelay) : IAsyncDisposable
    {
        private readonly List<GrpcChannel> _channels = [];
        public TaskCompletionSource RequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<(string Operation, string Deadline, string ApiKey)> Calls { get; } = new();
        public string Address { get; private set; } = string.Empty;

        public static async Task<BoundaryRuntime> StartAsync(string stalledOperation, TimeSpan replyDelay = default)
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName("localhost");
            names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
            using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            var certificate = X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), null);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0, listener =>
            {
                listener.Protocols = HttpProtocols.Http2;
                listener.UseHttps(certificate);
            }));
            var app = builder.Build();
            var runtime = new BoundaryRuntime(app, certificate, stalledOperation, replyDelay);
            app.MapPost("/llama.v2.Generator/{operation}", runtime.HandleAsync);
            try
            {
                await app.StartAsync();
                runtime.Address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
                return runtime;
            }
            catch
            {
                await runtime.DisposeAsync();
                throw;
            }
        }

        public GrpcLlamaRuntimeClient CreateClient(RuntimeTimeoutOptions timeouts)
        {
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, serverCertificate, _, _) =>
                    serverCertificate?.GetCertHashString() == certificate.GetCertHashString()
            };
            var httpClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            httpClient.DefaultRequestHeaders.Add("x-api-key", "boundary-runtime-key");
            var channel = GrpcChannel.ForAddress(Address, new GrpcChannelOptions { HttpClient = httpClient, DisposeHttpClient = true });
            _channels.Add(channel);
            return new GrpcLlamaRuntimeClient("generation", NullLogger<GrpcLlamaRuntimeClient>.Instance,
                new GrpcLlamaTransport(new Generator.GeneratorClient(channel), timeouts));
        }

        private async Task HandleAsync(HttpContext context)
        {
            var operation = context.Request.RouteValues["operation"]!.ToString()!;
            Calls.Enqueue((operation, context.Request.Headers["grpc-timeout"].ToString(), context.Request.Headers["x-api-key"].ToString()));
            RequestStarted.TrySetResult();
            try
            {
                await Task.Delay(operation == stalledOperation ? Timeout.InfiniteTimeSpan : replyDelay, context.RequestAborted);
                IMessage reply = operation switch
                {
                    "EstimateTokens" => new EstimateTokensReply { TokenCount = 1, ContextSize = 100, ReservedOutputTokens = 10, MaxAllowedInputTokens = 90, Fits = true },
                    "GetCapabilities" => new GetCapabilitiesReply { ModelId = "test-model", SupportsStructuredOutput = true, SupportsJsonOutput = true },
                    "Generate" => new GenerateReply { Model = "test-model", Content = "{\"ok\":true}", RuntimeTrace = new RuntimeTrace { StructuredOutputApplied = true, StructuredOutputSatisfied = true } },
                    _ => throw new InvalidOperationException()
                };
                var payload = reply.ToByteArray();
                var prefix = new byte[5];
                BinaryPrimitives.WriteInt32BigEndian(prefix.AsSpan(1), payload.Length);
                context.Response.ContentType = "application/grpc";
                context.Response.DeclareTrailer("grpc-status");
                await context.Response.Body.WriteAsync(prefix, context.RequestAborted);
                await context.Response.Body.WriteAsync(payload, context.RequestAborted);
                context.Response.AppendTrailer("grpc-status", "0");
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                CancellationObserved.TrySetResult();
            }
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var channel in _channels)
            {
                channel.Dispose();
            }
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await app.StopAsync(shutdown.Token);
            await app.DisposeAsync();
            certificate.Dispose();
        }
    }
}
