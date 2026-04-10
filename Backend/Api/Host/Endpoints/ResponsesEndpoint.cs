using GenProxy.Api.Services.Contracts;
using GenProxy.Api.Services.Contracts.Models;

namespace GenProxy.Api.Host.Endpoints;

public static class ResponsesEndpoint
{
    public static void MapResponsesEndpoint(this IEndpointRouteBuilder builder)
    {
        builder.MapPost("v1/responses", async (
                ResponseCreateRequest request,
                IResponseGenerationService service,
                CancellationToken cancellationToken) =>
            {
                var command = new ResponseCreateCommand(
                    request.Model,
                    request.Input,
                    request.MaxOutputTokens,
                    request.Metadata);

                var result = await service.GenerateAsync(command, cancellationToken);
                return Results.Ok(ResponsesFactory.ToResponse(result));
            })
            .WithTags("Responses")
            .WithName("CreateResponse")
            .WithSummary("Create a response from the configured generation runtime.")
            .WithDescription("Accepts a partial Responses API-compatible request body. `model` and `input` are required. `max_output_tokens` and `metadata` are accepted for compatibility, but output token accounting is not yet returned by the runtime.")
            .AddEndpointFilter<Validation.ValidationEndpointFilter<ResponseCreateRequest>>()
            .RequireRateLimiting("public-api");
    }
}
