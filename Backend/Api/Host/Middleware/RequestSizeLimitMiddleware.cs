using GenProxy.Api.Host.Validation;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace GenProxy.Api.Host.Middleware;

public sealed class RequestSizeLimitMiddleware(
    RequestDelegate next,
    IOptions<RequestLimitsOptions> options,
    IProblemDetailsService problemDetailsService)
{
    private static readonly PathString ResponsesPath = new("/v1/responses");
    private readonly RequestDelegate _next = next;
    private readonly RequestLimitsOptions _options = options.Value;
    private readonly IProblemDetailsService _problemDetailsService = problemDetailsService;

    public async Task InvokeAsync(HttpContext context)
    {
        if (HttpMethods.IsPost(context.Request.Method) && context.Request.Path == ResponsesPath)
        {
            var maxRequestBodySizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (maxRequestBodySizeFeature is { IsReadOnly: false })
            {
                maxRequestBodySizeFeature.MaxRequestBodySize = _options.MaxRequestBodyBytes;
            }

            if (context.Request.ContentLength is long contentLength &&
                contentLength > _options.MaxRequestBodyBytes)
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;

                await _problemDetailsService.TryWriteAsync(new ProblemDetailsContext
                {
                    HttpContext = context,
                    ProblemDetails = new ProblemDetails
                    {
                        Title = "Request body too large.",
                        Detail = $"Request body exceeds the configured limit of {_options.MaxRequestBodyBytes} bytes.",
                        Status = StatusCodes.Status413PayloadTooLarge,
                        Type = "https://httpstatuses.com/413"
                    }
                });

                return;
            }
        }

        await _next(context);
    }
}
