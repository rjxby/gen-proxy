using GenProxy.Api.Host.Middleware;
using GenProxy.Api.Host.Validation;

namespace GenProxy.Api.Host.Configurations;

public static class SetupPresentationLayer
{
    public static IServiceCollection AddPresentationLayer(this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<RequestLimitsOptions>()
            .Bind(configuration.GetSection(RequestLimitsOptions.SectionName))
            .Validate(
                options => options.MaxRequestBodyBytes > 0 &&
                    options.MaxModelCharacters > 0 &&
                    options.MaxInputCharacters > 0 &&
                    options.MaxMetadataEntries >= 0 &&
                    options.MaxMetadataKeyCharacters > 0 &&
                    options.MaxMetadataValueCharacters > 0,
                "Request limits must be positive values.")
            .ValidateOnStart();

        services.AddSwagger();
        services.AddProblemDetails();
        services.AddExceptionHandler<ExceptionHandler>();
        services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower;
        });

        return services;
    }
}
