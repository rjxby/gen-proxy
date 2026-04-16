using GenProxy.Api.Host.Middleware;
using GenProxy.Api.Host.Validation;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.AspNetCore.Routing;

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
        services.AddHttpLogging(options =>
        {
            options.LoggingFields = HttpLoggingFields.None;
            options.CombineLogs = true;
            options.RequestBodyLogLimit = 4096;
            options.ResponseBodyLogLimit = 4096;

            options.RequestHeaders.Clear();
            options.RequestHeaders.Add("Content-Type");
            options.RequestHeaders.Add("Content-Length");
            options.RequestHeaders.Add("Accept");

            options.ResponseHeaders.Clear();
            options.ResponseHeaders.Add("Content-Type");
            options.ResponseHeaders.Add("Content-Length");

            options.MediaTypeOptions.Clear();
            options.MediaTypeOptions.AddText("application/json");
            options.MediaTypeOptions.AddText("application/problem+json");
            options.MediaTypeOptions.AddText("text/plain");
        });
        services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
        services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower;
        });

        return services;
    }
}
