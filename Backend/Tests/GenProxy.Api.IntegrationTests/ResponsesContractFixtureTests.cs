using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
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

public class ResponsesContractFixtureTests
{
    [Theory]
    [InlineData("response", 200)]
    [InlineData("unsupported-stream", 400)]
    [InlineData("openapi", 200)]
    public async Task HttpContracts_MatchReviewedFixtures(string name, int expectedStatus)
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ApiKeys:Keys:0"] = "contract-test-key",
                ["GenerationRuntime:Address"] = "https://localhost:50051",
                ["PromptReducerRuntime:Enabled"] = "false"
            }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IResponseGenerationService>();
                services.AddSingleton<IResponseGenerationService, ContractResponse>();
            });
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        client.DefaultRequestHeaders.Add("x-api-key", "contract-test-key");
        using var response = name == "openapi"
            ? await client.GetAsync("/swagger/v1/swagger.json")
            : await client.PostAsJsonAsync("/v1/responses", new
            {
                model = "contract-model",
                stream = name == "unsupported-stream",
                input = new[] { new { type = "message", role = "user", content = new[] { new { type = "input_text", text = "contract input" } } } }
            });
        ((int)response.StatusCode).Should().Be(expectedStatus);
        var actual = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        if (name == "response")
        {
            actual["id"] = "<response-id>";
            actual["created_at"] = 0;
            actual["output"]![0]!["id"] = "<message-id>";
        }
        if (actual["trace_id"] is not null)
        {
            actual["trace_id"] = "<trace-id>";
        }

        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Backend/GenProxy.sln")))
        {
            root = root.Parent;
        }
        root.Should().NotBeNull();
        var expectedPath = Path.Combine(root!.FullName, "Backend/Tests/GenProxy.Api.IntegrationTests/Fixtures", name + ".json");
        var expected = JsonNode.Parse(await File.ReadAllTextAsync(expectedPath));
        var matches = JsonNode.DeepEquals(expected, actual);
        if (!matches)
        {
            var actualPath = Path.Combine(Path.GetTempPath(), $"gen-proxy-contract-{name}.json");
            await File.WriteAllTextAsync(actualPath, actual.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
            matches.Should().BeTrue($"review the contract change; actual JSON is at {actualPath}; expected fixture is {expectedPath}");
        }
    }

    private sealed class ContractResponse : IResponseGenerationService
    {
        public Task<GeneratedResponse> GenerateAsync(ResponseCreateCommand command, CancellationToken cancellationToken) =>
            Task.FromResult(new GeneratedResponse("resp_contract", command.Model, "contract output", false,
                PromptReductionStrategy.None, 3, 4, 7, 100, DateTimeOffset.FromUnixTimeSeconds(1000)));
    }
}
