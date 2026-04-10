using GenProxy.Api.Integrations.Contracts;
using GenProxy.Api.Host.Security;
using Microsoft.Extensions.Options;

namespace GenProxy.Api.Host.Middleware;

public sealed class ApiKeyAuthenticationMiddleware(
    RequestDelegate next,
    IOptions<ApiKeyOptions> options,
    IHostEnvironment environment,
    ILogger<ApiKeyAuthenticationMiddleware> logger)
{
    private readonly RequestDelegate _next = next;
    private readonly ApiKeyOptions _options = options.Value;
    private readonly IHostEnvironment _environment = environment;
    private readonly ILogger<ApiKeyAuthenticationMiddleware> _logger = logger;

    public async Task InvokeAsync(HttpContext context)
    {
        if (_options.Keys.Count == 0)
        {
            if (_environment.IsDevelopment() && _options.AllowUnauthenticatedInDevelopment)
            {
                await _next(context);
                return;
            }

            _logger.LogWarning("Request rejected because API key authentication is not configured.");
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Missing API key." });
            return;
        }

        if (!context.Request.Headers.TryGetValue(Constants.Auth.ApiKeyHeaderName, out var providedHeader))
        {
            GenProxyMetrics.AuthenticationFailures.Add(1, KeyValuePair.Create<string, object?>("reason", "missing"));
            _logger.LogWarning("Request rejected because the API key header was missing.");
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Missing API key." });
            return;
        }

        var token = providedHeader.ToString();

        if (!_options.Keys.Contains(token))
        {
            GenProxyMetrics.AuthenticationFailures.Add(1, KeyValuePair.Create<string, object?>("reason", "invalid"));
            _logger.LogWarning("Request rejected because the API key was invalid.");
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Invalid API key." });
            return;
        }

        context.Items[Constants.Auth.ApiKeyContextItemKey] = token;
        await _next(context);
    }
}
