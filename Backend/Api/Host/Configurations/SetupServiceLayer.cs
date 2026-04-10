using FluentValidation;
using GenProxy.Api.Host.Validation;
using GenProxy.Api.Services.Contracts;
using GenProxy.Api.Services.Implementation.Configuration;
using GenProxy.Api.Services.Implementation.Services;

namespace GenProxy.Api.Host.Configurations;

public static class SetupServiceLayer
{
    public static IServiceCollection AddServiceLayer(this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<PromptReductionOptions>()
            .Bind(configuration.GetSection(PromptReductionOptions.SectionName))
            .Validate(
                options => !options.UsePromptReducerRuntime || !string.IsNullOrWhiteSpace(options.SummarizationPromptTemplate),
                "Prompt reduction template is required when the prompt reducer runtime is enabled.")
            .ValidateOnStart();

        services.AddTransient<IResponseGenerationService, ResponseGenerationService>();
        services.AddSingleton<IPromptReductionPipeline, PromptReductionPipeline>();
        services.AddSingleton<IPromptReducer, LlmPromptSummarizer>();
        services.AddSingleton<IPromptReducer, LeadingPromptTruncator>();
        services.AddValidatorsFromAssemblyContaining<ResponseCreateRequestValidator>();

        return services;
    }
}
