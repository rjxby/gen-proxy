using GenProxy.Api.Host.Validation;
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
                var command = ResponseCreateRequestMapper.Map(request);
                var result = await service.GenerateAsync(command, cancellationToken);
                return Results.Ok(ResponsesFactory.ToResponse(result));
            })
            .WithTags("Responses")
            .WithName("CreateResponse")
            .WithSummary("Create a response from the configured generation runtime.")
            .WithDescription(
                $"Accepts a Responses-compatible request body. `model` is required. `input` supports exactly one user message with `type: message` and one or more `input_text` content parts. Top-level string input is not supported. `response_format.type` supports `{RequestedResponseFormats.Text}` and `{RequestedResponseFormats.JsonSchema}`. `json_schema` response formats are forwarded to the runtime as schema-constrained structured JSON output. `temperature`, `top_p`, and `max_output_tokens` are forwarded to the runtime, and support for those fields is runtime-defined. The current local `llama-runtime` stack uses greedy decoding and only accepts `max_output_tokens` when it matches the runtime's configured `GenerationMaxNewTokens`. Multi-turn structured input, `tools`, `tool_choice`, and streaming are not supported yet.")
            .AddEndpointFilter<Validation.ValidationEndpointFilter<ResponseCreateRequest>>()
            .RequireRateLimiting("public-api");
    }
}
