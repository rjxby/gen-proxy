using GenProxy.Api.Services.Contracts.Models;

namespace GenProxy.Api.Host.Endpoints;

public static class ResponseCreateRequestMapper
{
    public static ResponseCreateCommand Map(ResponseCreateRequest request)
    {
        return new ResponseCreateCommand(
            request.Model,
            BuildPrompt(request.Input),
            request.MaxOutputTokens,
            request.Temperature,
            request.TopP,
            request.Metadata,
            MapResponseFormat(request.ResponseFormat?.Type),
            MapJsonSchema(request.ResponseFormat));
    }

    private static string BuildPrompt(IReadOnlyList<ResponseInputMessageRequest> input)
    {
        return string.Join('\n', input[0].Content.Select(part => part.Text));
    }

    private static RequestedResponseFormat? MapResponseFormat(string? responseFormatType)
    {
        return responseFormatType switch
        {
            RequestedResponseFormats.Text => RequestedResponseFormat.Text,
            RequestedResponseFormats.JsonSchema => RequestedResponseFormat.JsonSchema,
            null => null,
            _ => null
        };
    }

    private static string? MapJsonSchema(ResponseFormatRequest? responseFormat)
    {
        if (responseFormat?.Type != RequestedResponseFormats.JsonSchema ||
            responseFormat.JsonSchema?.Schema is not { } schema)
        {
            return null;
        }

        return schema.GetRawText();
    }
}
