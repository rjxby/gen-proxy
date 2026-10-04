using GenProxy.Api.Integrations.Contracts;
using GenProxy.Api.Integrations.Contracts.Configuration;
using GenProxy.Api.Integrations.Implementation.Clients;
using GenProxy.Api.Integrations.Implementation.Configuration;

namespace GenProxy.Api.Host.Configurations;

public static class SetupIntegrationLayer
{
    public static IServiceCollection AddIntegrationLayer(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services
            .AddOptions<GenerationRuntimeOptions>()
            .Bind(configuration.GetSection(GenerationRuntimeOptions.SectionName))
            .Validate(
                options => IsValidRuntimeAddress(options.Address),
                "Generation runtime address must be an absolute HTTPS URI.")
            .Validate(options => options.Timeouts.IsValid(), "Generation runtime timeouts must be greater than zero and no longer than one day.")
            .ValidateOnStart();

        services
            .AddOptions<PromptReducerRuntimeOptions>()
            .Bind(configuration.GetSection(PromptReducerRuntimeOptions.SectionName))
            .Validate(
                options => !options.Enabled || IsValidRuntimeAddress(options.Address),
                "Prompt reducer runtime address must be an absolute HTTPS URI.")
            .Validate(options => !options.Enabled || options.Timeouts.IsValid(), "Prompt reducer runtime timeouts must be greater than zero and no longer than one day.")
            .ValidateOnStart();

        services.AddSingleton<IGenerationRuntimeClient>(serviceProvider =>
        {
            var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<GenerationRuntimeOptions>>().Value;
            var runtimeClient = CreateGrpcRuntimeClient(serviceProvider, "generation", options.Address, options.ApiKey, options.Timeouts);

            return new GenerationRuntimeClient(runtimeClient, ownsInner: true);
        });

        var isPromptReducerRuntimeEnabled = PromptReducerRuntimeConfiguration.IsEnabled(configuration);
        if (isPromptReducerRuntimeEnabled)
        {
            services.AddSingleton<IPromptReducerRuntimeClient>(serviceProvider =>
            {
                var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<PromptReducerRuntimeOptions>>().Value;
                var runtimeClient = CreateGrpcRuntimeClient(serviceProvider, "prompt_reducer", options.Address, options.ApiKey, options.Timeouts);

                return new PromptReducerRuntimeClient(runtimeClient, ownsInner: true);
            });
        }

        return services;
    }

    private static GrpcLlamaRuntimeClient CreateGrpcRuntimeClient(
        IServiceProvider serviceProvider,
        string runtimeName,
        string address,
        string? apiKey,
        RuntimeTimeoutOptions timeouts)
    {
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        return new GrpcLlamaRuntimeClient(
            runtimeName,
            address,
            loggerFactory.CreateLogger<GrpcLlamaRuntimeClient>(),
            apiKey,
            timeouts);
    }

    private static bool IsValidRuntimeAddress(string address)
    {
        return Uri.TryCreate(address, UriKind.Absolute, out var uri) &&
            string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
    }
}
