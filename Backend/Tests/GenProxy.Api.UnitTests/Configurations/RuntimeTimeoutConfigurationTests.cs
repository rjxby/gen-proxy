using FluentAssertions;
using GenProxy.Api.Host.Configurations;
using GenProxy.Api.Integrations.Contracts.Configuration;
using GenProxy.Api.Integrations.Implementation.Clients;
using GenProxy.Api.Integrations.Implementation.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace GenProxy.Api.UnitTests;

public class RuntimeTimeoutConfigurationTests
{
    [Fact]
    public void RuntimeTimeouts_DefaultsBoundEveryOperation()
    {
        var timeouts = new RuntimeTimeoutOptions();

        timeouts.EstimateTokens.Should().Be(TimeSpan.FromSeconds(10));
        timeouts.GetCapabilities.Should().Be(TimeSpan.FromSeconds(10));
        timeouts.Generate.Should().Be(TimeSpan.FromSeconds(120));
        new ResponsesTimeoutOptions().Timeout.Should().Be(TimeSpan.FromSeconds(180));
    }

    [Theory]
    [InlineData("GenerationRuntime", "EstimateTokens", "00:00:00")]
    [InlineData("GenerationRuntime", "GetCapabilities", "-00:00:01")]
    [InlineData("GenerationRuntime", "Generate", "1.00:00:01")]
    [InlineData("PromptReducerRuntime", "EstimateTokens", "00:00:00")]
    [InlineData("PromptReducerRuntime", "GetCapabilities", "-00:00:01")]
    [InlineData("PromptReducerRuntime", "Generate", "1.00:00:01")]
    public void RuntimeTimeouts_RejectInvalidConfiguredDurations(string section, string operation, string value)
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            [$"{section}:Timeouts:{operation}"] = value
        });
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIntegrationLayer(configuration);
        using var provider = services.BuildServiceProvider();

        Action resolve = section == GenerationRuntimeOptions.SectionName
            ? () => _ = provider.GetRequiredService<IOptions<GenerationRuntimeOptions>>().Value
            : () => _ = provider.GetRequiredService<IOptions<PromptReducerRuntimeOptions>>().Value;

        resolve.Should().Throw<OptionsValidationException>();
    }

    [Theory]
    [InlineData("00:00:00")]
    [InlineData("-00:00:01")]
    [InlineData("1.00:00:01")]
    public void ResponsesTimeout_RejectsInvalidConfiguredDurations(string value)
    {
        var services = new ServiceCollection();
        services.AddPresentationLayer(Configuration(new Dictionary<string, string?> { ["ResponsesTimeout:Timeout"] = value }));
        using var provider = services.BuildServiceProvider();

        var resolve = () => provider.GetRequiredService<IOptions<ResponsesTimeoutOptions>>().Value;

        resolve.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void RuntimeTimeouts_BindIndependentlyForBothRuntimes()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIntegrationLayer(Configuration(new Dictionary<string, string?>
        {
            ["GenerationRuntime:Timeouts:Generate"] = "00:02:30",
            ["PromptReducerRuntime:Timeouts:Generate"] = "00:00:30"
        }));
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IOptions<GenerationRuntimeOptions>>().Value.Timeouts.Generate.Should().Be(TimeSpan.FromSeconds(150));
        provider.GetRequiredService<IOptions<PromptReducerRuntimeOptions>>().Value.Timeouts.Generate.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void RuntimeTimeouts_WhenReducerDisabled_IgnoreReducerTimeouts()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIntegrationLayer(Configuration(new Dictionary<string, string?>
        {
            ["PromptReducerRuntime:Enabled"] = "false",
            ["PromptReducerRuntime:Timeouts:Generate"] = "00:00:00"
        }));
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IOptions<PromptReducerRuntimeOptions>>().Value.Enabled.Should().BeFalse();
    }

    [Fact]
    public void AuthenticatedHttpClient_UsesGrpcDeadlinesInsteadOfImplicitHttpTimeout()
    {
        using var client = GrpcLlamaTransport.CreateHttpClient("https://runtime.test", "runtime-key");

        client.Should().NotBeNull();
        client!.Timeout.Should().Be(Timeout.InfiniteTimeSpan);
        client.DefaultRequestHeaders.GetValues("x-api-key").Should().ContainSingle("runtime-key");
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
