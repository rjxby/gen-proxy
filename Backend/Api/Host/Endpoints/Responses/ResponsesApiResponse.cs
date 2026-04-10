using System.Text.Json.Serialization;

namespace GenProxy.Api.Host.Endpoints;

public record ResponsesApiResponse(
    string Id,
    string Object,
    [property: JsonPropertyName("created_at")] long CreatedAt,
    string Status,
    string Model,
    IReadOnlyList<ResponseOutputItem> Output,
    [property: JsonPropertyName("output_text")] string OutputText,
    ResponseUsage Usage);
