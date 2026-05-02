using GenProxy.Api.Services.Contracts.Models;

namespace GenProxy.Api.Host.Endpoints;

internal static class ResponsesFactory
{
    public static ResponsesApiResponse ToResponse(GeneratedResponse result)
    {
        return new ResponsesApiResponse(
            result.ResponseId,
            ResponseObjectType.Response,
            result.CreatedAt.ToUnixTimeSeconds(),
            ResponseStatus.Completed,
            result.Model,
            [
                new ResponseOutputItem(
                    $"msg_{Guid.NewGuid():N}",
                    ResponseItemType.Message,
                    ResponseStatus.Completed,
                    ResponseRole.Assistant,
                    [new ResponseContentPart(ResponseContentPartType.OutputText, result.OutputText)])
            ],
            result.OutputText,
            new ResponseUsage(result.InputTokens, result.OutputTokens, result.TotalTokens));
    }
}
