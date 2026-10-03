using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace GenProxy.Api.Host.Middleware;

internal static class ResponsesTimeoutResponse
{
    public static Task WriteAsync(HttpContext context)
    {
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status504GatewayTimeout,
            Title = "Response request timed out.",
            Detail = "The response request exceeded the configured time limit.",
            Type = "https://httpstatuses.com/504"
        };
        problem.Extensions["trace_id"] = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
        context.Response.ContentType = "application/problem+json";

        // RequestAborted includes the expired timeout token. The timeout response must still reach the client.
        return JsonSerializer.SerializeAsync(context.Response.Body, problem, cancellationToken: CancellationToken.None);
    }
}
