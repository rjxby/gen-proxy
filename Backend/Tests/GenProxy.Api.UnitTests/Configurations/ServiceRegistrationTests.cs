using FluentAssertions;
using GenProxy.Api.Host.Configurations;
using GenProxy.Api.Integrations.Contracts;
using GenProxy.Api.Services.Contracts;
using GenProxy.Api.Services.Implementation.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace GenProxy.Api.UnitTests;

public class ServiceRegistrationTests
{
    [Fact]
    public void AddLayers_WhenPromptReducerRuntimeDisabled_ResolvesResponseGenerationServiceWithoutReducerClient()
    {
        var services = CreateServiceCollection(
            promptReducerRuntimeEnabled: false,
            promptReducerRuntimeAddress: string.Empty);
        using var serviceProvider = services.BuildServiceProvider(validateScopes: true);

        var service = serviceProvider.GetRequiredService<IResponseGenerationService>();
        var reducers = serviceProvider.GetServices<IPromptReducer>().ToList();
        var reducerRuntimeClient = serviceProvider.GetService<IPromptReducerRuntimeClient>();

        service.Should().NotBeNull();
        reducerRuntimeClient.Should().BeNull();
        reducers.Should().ContainSingle(reducer => reducer is LeadingPromptTruncator);
        reducers.Should().NotContain(reducer => reducer is LlmPromptSummarizer);
    }

    [Fact]
    public void AddLayers_WhenPromptReducerRuntimeEnabled_RegistersReducerClientAndSummarizer()
    {
        var services = CreateServiceCollection(
            promptReducerRuntimeEnabled: true,
            promptReducerRuntimeAddress: "https://localhost:50052");
        using var serviceProvider = services.BuildServiceProvider(validateScopes: true);

        var reducers = serviceProvider.GetServices<IPromptReducer>().ToList();
        var reducerRuntimeClient = serviceProvider.GetService<IPromptReducerRuntimeClient>();

        reducerRuntimeClient.Should().NotBeNull();
        reducers.Should().Contain(reducer => reducer is LeadingPromptTruncator);
        reducers.Should().Contain(reducer => reducer is LlmPromptSummarizer);
    }

    private static IServiceCollection CreateServiceCollection(
        bool promptReducerRuntimeEnabled,
        string promptReducerRuntimeAddress)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GenerationRuntime:Address"] = "https://localhost:50051",
                ["PromptReducerRuntime:Enabled"] = promptReducerRuntimeEnabled.ToString(),
                ["PromptReducerRuntime:Address"] = promptReducerRuntimeAddress,
                ["PromptReduction:SummarizationPromptTemplate"] = "Summarize {{prompt}} to {{max_tokens}}",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIntegrationLayer(configuration);
        services.AddServiceLayer(configuration);

        return services;
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "GenProxy.Api.UnitTests";

        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
