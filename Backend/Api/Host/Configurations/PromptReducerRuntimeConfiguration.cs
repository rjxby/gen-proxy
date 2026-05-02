using GenProxy.Api.Integrations.Contracts.Configuration;

namespace GenProxy.Api.Host.Configurations;

internal static class PromptReducerRuntimeConfiguration
{
    public static bool IsEnabled(IConfiguration configuration)
    {
        return configuration
            .GetSection(PromptReducerRuntimeOptions.SectionName)
            .GetValue<bool?>(nameof(PromptReducerRuntimeOptions.Enabled)) ?? true;
    }
}
