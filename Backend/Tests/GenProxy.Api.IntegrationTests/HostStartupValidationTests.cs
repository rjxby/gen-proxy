using FluentAssertions;
using GenProxy.Api.Host.Configurations;
using GenProxy.Api.Host.Security;
using GenProxy.Api.Integrations.Contracts.Configuration;
using GenProxy.Api.Integrations.Implementation.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace GenProxy.Api.IntegrationTests;

public class HostStartupValidationTests
{
    [Theory]
    [InlineData("GenerationRuntime", "not-a-valid-uri")]
    [InlineData("GenerationRuntime", "http://localhost:50051")]
    [InlineData("PromptReducerRuntime", "not-a-valid-uri")]
    [InlineData("PromptReducerRuntime", "http://localhost:50052")]
    public async Task StartAsync_WithInvalidRuntimeAddress_FailsStartupValidation(string section, string address)
    {
        await using var app = CreateApplication(new Dictionary<string, string?>
        {
            [$"{section}:Address"] = address
        });

        var act = () => app.StartAsync();

        var failures = (await act.Should().ThrowAsync<OptionsValidationException>()).Subject;
        var expectedOptionsType = section == GenerationRuntimeOptions.SectionName
            ? typeof(GenerationRuntimeOptions)
            : typeof(PromptReducerRuntimeOptions);
        var runtimeName = section == GenerationRuntimeOptions.SectionName ? "Generation" : "Prompt reducer";
        // The reducer options are also validated through the prompt-reduction options dependency.
        failures.Should().NotBeEmpty().And.AllSatisfy(failure =>
        {
            failure.OptionsType.Should().Be(expectedOptionsType);
            failure.Failures.Should().Contain($"{runtimeName} runtime address must be an absolute HTTPS URI.");
        });
    }

    [Fact]
    public async Task StartAsync_WithoutConfiguredApiKeys_FailsStartupValidation()
    {
        await using var app = CreateApplication(new Dictionary<string, string?>
        {
            ["ApiKeys:Keys:0"] = null
        });

        var act = () => app.StartAsync();

        var failure = (await act.Should().ThrowAsync<OptionsValidationException>()).Which;
        failure.OptionsType.Should().Be(typeof(ApiKeyOptions));
        failure.Failures.Should().Contain("At least one API key is required unless unauthenticated access is explicitly enabled in Development.");
    }

    [Fact]
    public async Task StartAsync_WithValidConfiguration_StartsSuccessfully()
    {
        await using var app = CreateApplication(new Dictionary<string, string?>());

        await app.StartAsync();
        await app.StopAsync();
    }

    private static WebApplication CreateApplication(Dictionary<string, string?> overrides)
    {
        // WebApplicationFactory runs the entry point concurrently. RunAsync disposes a failed
        // host and can mask its validation exception before the factory observes startup.
        // Start directly so each test owns disposal and observes the original startup failure.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing"
        });
        builder.Configuration.Sources.Clear();
        var settings = new Dictionary<string, string?>
        {
            ["ApiKeys:Keys:0"] = "test-api-key",
            ["GenerationRuntime:Address"] = "https://localhost:50051",
            ["PromptReducerRuntime:Enabled"] = "true",
            ["PromptReducerRuntime:Address"] = "https://localhost:50052"
        };
        foreach (var setting in overrides)
        {
            if (setting.Value is null)
            {
                settings.Remove(setting.Key);
            }
            else
            {
                settings[setting.Key] = setting.Value;
            }
        }

        builder.Configuration.AddInMemoryCollection(settings);
        builder.WebHost.UseTestServer();
        builder.Services.AddSecurity(builder.Configuration, builder.Environment);
        builder.Services.AddIntegrationLayer(builder.Configuration);
        builder.Services.AddServiceLayer(builder.Configuration);
        builder.Services.AddPresentationLayer(builder.Configuration);

        return builder.Build();
    }
}
