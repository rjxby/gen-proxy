using FluentValidation;
using GenProxy.Api.Host.Validation;
using GenProxy.Api.Integrations.Contracts.Configuration;
using GenProxy.Api.Services.Contracts;
using GenProxy.Api.Services.Implementation.Configuration;
using GenProxy.Api.Services.Implementation.Services;
using Microsoft.Extensions.Options;

namespace GenProxy.Api.Host.Configurations;

public static class SetupServiceLayer
{
    public static IServiceCollection AddServiceLayer(this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<PromptReductionOptions>()
            .Bind(configuration.GetSection(PromptReductionOptions.SectionName))
            .Validate<IOptions<PromptReducerRuntimeOptions>>(
                (options, runtimeOptions) => !runtimeOptions.Value.Enabled || !string.IsNullOrWhiteSpace(options.SummarizationPromptTemplate),
                "Prompt reduction template is required when the prompt reducer runtime is enabled.")
            .ValidateOnStart();

        services.AddTransient<IResponseGenerationService, ResponseGenerationService>();
        services.AddSingleton<IPromptReductionPipeline, PromptReductionPipeline>();
        services.AddSingleton<IPromptReducer, LeadingPromptTruncator>();

        var isPromptReducerRuntimeEnabled = PromptReducerRuntimeConfiguration.IsEnabled(configuration);
        if (isPromptReducerRuntimeEnabled)
        {
            services.AddSingleton<IPromptReducer, LlmPromptSummarizer>();
        }

        services.AddValidatorsFromAssemblyContaining<ResponseCreateRequestValidator>();

        return services;
    }
}
