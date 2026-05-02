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
            .ValidateOnStart();

        services
            .AddOptions<PromptReducerRuntimeOptions>()
            .Bind(configuration.GetSection(PromptReducerRuntimeOptions.SectionName))
            .Validate(
                options => !options.Enabled || IsValidRuntimeAddress(options.Address),
                "Prompt reducer runtime address must be an absolute HTTPS URI.")
            .ValidateOnStart();

        services.AddSingleton<IGenerationRuntimeClient>(serviceProvider =>
        {
            var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<GenerationRuntimeOptions>>().Value;
            var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
            var runtimeClient = new GrpcLlamaRuntimeClient(
                runtimeName: "generation",
                address: options.Address,
                logger: loggerFactory.CreateLogger<GrpcLlamaRuntimeClient>(),
                apiKey: options.ApiKey);

            return new GenerationRuntimeClient(runtimeClient);
        });

        var isPromptReducerRuntimeEnabled = PromptReducerRuntimeConfiguration.IsEnabled(configuration);
        if (isPromptReducerRuntimeEnabled)
        {
            services.AddSingleton<IPromptReducerRuntimeClient>(serviceProvider =>
            {
                var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<PromptReducerRuntimeOptions>>().Value;
                var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
                var runtimeClient = new GrpcLlamaRuntimeClient(
                    runtimeName: "prompt_reducer",
                    address: options.Address,
                    logger: loggerFactory.CreateLogger<GrpcLlamaRuntimeClient>(),
                    apiKey: options.ApiKey);

                return new PromptReducerRuntimeClient(runtimeClient);
            });
        }

        return services;
    }

    private static bool IsValidRuntimeAddress(string address)
    {
        return Uri.TryCreate(address, UriKind.Absolute, out var uri) &&
            string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
    }
}
