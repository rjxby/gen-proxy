using GenProxy.Api.Integrations.Contracts;
using GenProxy.Api.Services.Contracts;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;

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
            BadHttpRequestException => (
                StatusCodes.Status400BadRequest,
                "Invalid request body.",
                "The request body could not be read as valid JSON.",
                LogLevel.Warning),
            ResponseFormatNotSupportedException ex => (
                StatusCodes.Status400BadRequest,
                "Unsupported response format.",
                ex.Message,
                LogLevel.Information),
            PromptBudgetExceededException ex => (
                StatusCodes.Status422UnprocessableEntity,
                "Prompt exceeds token budget.",
                ex.Message,
                LogLevel.Information),
            StructuredOutputNotSatisfiedException ex => (
                StatusCodes.Status502BadGateway,
                "Structured output requirement not satisfied.",
                ex.Message,
                LogLevel.Warning),
            LlamaRuntimeStructuredOutputNotSatisfiedException ex => (
                StatusCodes.Status502BadGateway,
                "Structured output requirement not satisfied.",
                ex.Message,
                LogLevel.Warning),
            PromptReductionFailedException ex => (
                StatusCodes.Status502BadGateway,
                "Prompt reduction failed.",
                ex.Message,
                LogLevel.Warning),
            LlamaRuntimePromptBudgetExceededException ex => (
                StatusCodes.Status422UnprocessableEntity,
                "Prompt exceeds token budget.",
                ex.Message,
                LogLevel.Information),
            LlamaRuntimeUnsupportedGenerationOverridesException ex => (
                StatusCodes.Status400BadRequest,
                "Unsupported generation overrides.",
                ex.Message,
                LogLevel.Information),
            LlamaRuntimeInvalidArgumentException ex => (
                StatusCodes.Status400BadRequest,
                "Invalid runtime request.",
                ex.Message,
                LogLevel.Information),
            LlamaRuntimeTimeoutException ex => (
                StatusCodes.Status504GatewayTimeout,
                "Upstream runtime timed out.",
                ex.Message,
                LogLevel.Warning),
            LlamaRuntimeCallException ex => (
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
        else if (exception is PromptBudgetExceededException or LlamaRuntimePromptBudgetExceededException or LlamaRuntimeUnsupportedGenerationOverridesException or LlamaRuntimeInvalidArgumentException or ResponseFormatNotSupportedException)
        {
            _logger.Log(logLevel, "{Title} {Detail}", title, detail);
        }
        else
        {
            _logger.Log(logLevel, exception, "{Title} {Detail}", title, detail);
        }

        httpContext.Response.StatusCode = statusCode;

        var problemDetails = new ProblemDetails
        {
            Title = title,
            Detail = detail,
            Status = statusCode,
            Type = $"https://httpstatuses.com/{statusCode}"
        };
        problemDetails.Extensions["trace_id"] = Activity.Current?.TraceId.ToString() ?? httpContext.TraceIdentifier;

        return await _problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problemDetails,
            Exception = exception
        });
    }
}
