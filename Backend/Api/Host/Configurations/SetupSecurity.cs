using GenProxy.Api.Host.Security;
using GenProxy.Api.Integrations.Contracts;
using System.Threading.RateLimiting;

namespace GenProxy.Api.Host.Configurations;

public static class SetupSecurity
{
    public static IServiceCollection AddSecurity(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        services
            .AddOptions<ApiKeyOptions>()
            .Bind(configuration.GetSection(ApiKeyOptions.SectionName))
            .Validate(
                options => options.Keys.All(key => !string.IsNullOrWhiteSpace(key)),
                "API keys must not contain blank values.")
            .Validate(
                options => options.Keys.Count > 0 ||
                    (environment.IsDevelopment() && options.AllowUnauthenticatedInDevelopment),
                "At least one API key is required unless unauthenticated access is explicitly enabled in Development.")
            .ValidateOnStart();

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, cancellationToken) =>
            {
                GenProxyMetrics.RateLimitRejections.Add(1);
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await context.HttpContext.Response.WriteAsJsonAsync(
                    new
                    {
                        title = "Rate limit exceeded.",
                        detail = "Too many requests were sent for this API key or client.",
                        status = StatusCodes.Status429TooManyRequests
                    },
                    cancellationToken);
            };
            options.AddPolicy("public-api", context =>
            {
                var partitionKey = context.Items.TryGetValue(Constants.Auth.ApiKeyContextItemKey, out var apiKey)
                    ? apiKey?.ToString() ?? "anonymous"
                    : context.Connection.RemoteIpAddress?.ToString() ?? "anonymous";

                return RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey,
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 30,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                        AutoReplenishment = true
                    });
            });
        });

        return services;
    }
}
