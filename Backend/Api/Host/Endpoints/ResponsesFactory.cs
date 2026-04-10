using GenProxy.Api.Services.Contracts.Models;

namespace GenProxy.Api.Host.Endpoints;

internal static class ResponsesFactory
{
    public static ResponsesApiResponse ToResponse(GeneratedResponse result)
    {
        return new ResponsesApiResponse(
            result.ResponseId,
            "response",
            result.CreatedAt.ToUnixTimeSeconds(),
            "completed",
            result.Model,
            [
                new ResponseOutputItem(
                    $"msg_{Guid.NewGuid():N}",
                    "message",
                    "assistant",
                    [new ResponseContentPart("output_text", result.OutputText)])
            ],
            result.OutputText,
            new ResponseUsage(result.InputTokens, null, null));
    }
}
