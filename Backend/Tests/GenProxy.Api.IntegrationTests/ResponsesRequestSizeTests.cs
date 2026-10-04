using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using GenProxy.Api.Host;
using GenProxy.Api.Services.Contracts;
using GenProxy.Api.Services.Contracts.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace GenProxy.Api.IntegrationTests;

public class ResponsesRequestSizeTests
{
    private const int MaxRequestBodyBytes = 131072;
    // Kestrel counts the chunk header, CRLF pairs, and terminator toward the body limit.
    private const int SingleChunkFramingBytes = 14;

    [Theory]
    [InlineData("/v1/responses", false)]
    [InlineData("/v1/responses/", false)]
    [InlineData("/V1/RESPONSES/", false)]
    [InlineData("/v1/responses", true)]
    [InlineData("/v1/responses/", true)]
    [InlineData("/V1/RESPONSES/", true)]
    public async Task PostResponses_WhenBodyExceedsLimit_RejectsBeforeValidationOrGeneration(
        string path,
        bool chunked)
    {
        var service = new RecordingResponseGenerationService();
        await using var factory = CreateFactory(service);
        using var client = factory.CreateClient();
        using var request = CreateRequest(path, CreateBody(MaxRequestBodyBytes + 1, model: ""), chunked);

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        service.CallCount.Should().Be(0);
    }

    [Theory]
    [InlineData("/v1/responses", false)]
    [InlineData("/v1/responses/", false)]
    [InlineData("/V1/RESPONSES/", false)]
    [InlineData("/v1/responses", true)]
    [InlineData("/v1/responses/", true)]
    [InlineData("/V1/RESPONSES/", true)]
    public async Task PostResponses_WhenObservedBodyBytesAreExactlyAtLimit_AllowsGeneration(
        string path,
        bool chunked)
    {
        var service = new RecordingResponseGenerationService();
        await using var factory = CreateFactory(service);
        using var client = factory.CreateClient();
        var bodyBytes = chunked ? MaxRequestBodyBytes - SingleChunkFramingBytes : MaxRequestBodyBytes;
        using var request = CreateRequest(path, CreateBody(bodyBytes), chunked);

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        service.CallCount.Should().Be(1);
    }

    [Theory]
    [InlineData("/v1/responses")]
    [InlineData("/v1/responses/")]
    [InlineData("/V1/RESPONSES/")]
    public async Task PostResponses_WhenChunkedJsonBytesEqualLimit_RejectsFramingBeyondLimit(string path)
    {
        var service = new RecordingResponseGenerationService();
        await using var factory = CreateFactory(service);
        using var client = factory.CreateClient();
        using var request = CreateRequest(path, CreateBody(MaxRequestBodyBytes), chunked: true);

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        service.CallCount.Should().Be(0);
    }

    [Theory]
    [InlineData("/v1/responses", false)]
    [InlineData("/v1/responses/", false)]
    [InlineData("/v1/responses", true)]
    [InlineData("/v1/responses/", true)]
    public async Task PostResponses_WhenBodyIsSmall_AllowsGeneration(string path, bool chunked)
    {
        var service = new RecordingResponseGenerationService();
        await using var factory = CreateFactory(service);
        using var client = factory.CreateClient();
        using var request = CreateRequest(path, CreateBody(256), chunked);

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        service.CallCount.Should().Be(1);
    }

    private static WebApplicationFactory<Program> CreateFactory(IResponseGenerationService service)
    {
        var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ApiKeys:Keys:0"] = "test-api-key",
                        ["RequestLimits:MaxRequestBodyBytes"] = MaxRequestBodyBytes.ToString(),
                        ["PromptReducerRuntime:Enabled"] = "false",
                        ["GenerationRuntime:Address"] = "https://localhost:50051",
                        ["ResponsesLogging:LogBodies"] = "false"
                    }));
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IResponseGenerationService>();
                    services.AddSingleton(service);
                });
            });

        // TestServer does not enforce chunked body limits. Set the listener explicitly;
        // UseKestrel(0) does not set the parent factory's port after WithWebHostBuilder.
        factory.UseKestrel(server => server.Listen(IPAddress.Loopback, 0));
        return factory;
    }

    private static HttpRequestMessage CreateRequest(string path, byte[] body, bool chunked)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = chunked ? new ChunkedContent(body) : new ByteArrayContent(body)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Add(Constants.Auth.ApiKeyHeaderName, "test-api-key");
        if (chunked)
        {
            request.Headers.TransferEncodingChunked = true;
        }

        return request;
    }

    private static byte[] CreateBody(int byteCount, string model = "stories15m")
    {
        var emptyPaddingBody = JsonSerializer.Serialize(new
        {
            model,
            input = new[]
            {
                new
                {
                    type = "message",
                    role = "user",
                    content = new[] { new { type = "input_text", text = "hello world" } }
                }
            },
            padding = ""
        });
        var paddingLength = byteCount - Encoding.UTF8.GetByteCount(emptyPaddingBody);
        return Encoding.UTF8.GetBytes(emptyPaddingBody.Replace(
            "\"padding\":\"\"",
            $"\"padding\":\"{new string('x', paddingLength)}\"",
            StringComparison.Ordinal));
    }

    private sealed class ChunkedContent(byte[] body) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(body).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class RecordingResponseGenerationService : IResponseGenerationService
    {
        public int CallCount { get; private set; }

        public Task<GeneratedResponse> GenerateAsync(ResponseCreateCommand command, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new GeneratedResponse(
                "resp_size_test",
                command.Model,
                "generated text",
                false,
                PromptReductionStrategy.None,
                2,
                2,
                4,
                512,
                DateTimeOffset.UtcNow));
        }
    }
}
