using System.Text.Json.Serialization;

namespace GenProxy.Api.Host.Endpoints;

public record ResponsesApiResponse(
    string Id,
    ResponseObjectType Object,
    [property: JsonPropertyName("created_at")] long CreatedAt,
    ResponseStatus Status,
    string Model,
    IReadOnlyList<ResponseOutputItem> Output,
    [property: JsonPropertyName("output_text")] string OutputText,
    ResponseUsage Usage);

public record ResponseOutputItem(
    string Id,
    ResponseItemType Type,
    ResponseStatus Status,
    ResponseRole Role,
    IReadOnlyList<ResponseContentPart> Content);

public record ResponseContentPart(ResponseContentPartType Type, string Text);

public record ResponseUsage(
    [property: JsonPropertyName("input_tokens")] int InputTokens,
    [property: JsonPropertyName("output_tokens")] int? OutputTokens,
    [property: JsonPropertyName("total_tokens")] int? TotalTokens);
