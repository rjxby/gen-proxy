using GenProxy.Api.Integrations.Contracts;
using GenProxy.Api.Services.Contracts;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace GenProxy.Api.Host.Middleware;

public sealed class ExceptionHandler(
    ILogger<ExceptionHandler> logger,
    IProblemDetailsService problemDetailsService) : IExceptionHandler
{
    private readonly ILogger<ExceptionHandler> _logger = logger;
    private readonly IProblemDetailsService _problemDetailsService = problemDetailsService;

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (statusCode, title, detail, logLevel) = exception switch
        {
            PromptBudgetExceededException ex => (
                StatusCodes.Status422UnprocessableEntity,
                "Prompt exceeds token budget.",
                ex.Message,
                LogLevel.Information),
            PromptReductionFailedException ex => (
                StatusCodes.Status502BadGateway,
                "Prompt reduction failed.",
                ex.Message,
                LogLevel.Warning),
            UpstreamPromptBudgetExceededException ex => (
                StatusCodes.Status422UnprocessableEntity,
                "Prompt exceeds token budget.",
                ex.Message,
                LogLevel.Information),
            UpstreamRuntimeException ex => (
                StatusCodes.Status503ServiceUnavailable,
                "Upstream runtime unavailable.",
                ex.Message,
                LogLevel.Warning),
            KeyNotFoundException ex => (
                StatusCodes.Status404NotFound,
                "Resource not found.",
                ex.Message,
                LogLevel.Information),
            _ => (
                StatusCodes.Status500InternalServerError,
                "Internal server error.",
                "An unexpected server error occurred.",
                LogLevel.Error)
        };

        if (logLevel == LogLevel.Error)
        {
            _logger.LogError(exception, "Unhandled request failure.");
        }
        else if (exception is PromptBudgetExceededException or UpstreamPromptBudgetExceededException)
        {
            _logger.Log(logLevel, "{Title} {Detail}", title, detail);
        }
        else
        {
            _logger.Log(logLevel, exception, "{Title} {Detail}", title, detail);
        }

        httpContext.Response.StatusCode = statusCode;

        return await _problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = new ProblemDetails
            {
                Title = title,
                Detail = detail,
                Status = statusCode,
                Type = $"https://httpstatuses.com/{statusCode}"
            },
            Exception = exception
        });
    }
}
