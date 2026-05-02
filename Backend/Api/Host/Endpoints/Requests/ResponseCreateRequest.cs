using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using GenProxy.Api.Host.OpenApi;
using GenProxy.Api.Services.Contracts.Models;

namespace GenProxy.Api.Host.Endpoints;

public class ResponseCreateRequest
{
    public required string Model { get; set; }

    [MinLength(1)]
    [MaxLength(1)]
    public required List<ResponseInputMessageRequest> Input { get; set; }

    [JsonPropertyName("response_format")]
    [OpenApiDescription("Optional response format override. When provided, type is required.")]
    public ResponseFormatRequest? ResponseFormat { get; set; }

    [JsonPropertyName("max_output_tokens")]
    public int? MaxOutputTokens { get; set; }

    public float? Temperature { get; set; }

    [JsonPropertyName("top_p")]
    public float? TopP { get; set; }

    public bool? Stream { get; set; }

    public JsonElement? Tools { get; set; }

    [JsonPropertyName("tool_choice")]
    public JsonElement? ToolChoice { get; set; }

    public Dictionary<string, string>? Metadata { get; set; }
}

public class ResponseInputMessageRequest
{
    [OpenApiStringEnum("message")]
    public required string Type { get; set; }

    [OpenApiStringEnum("user")]
    public required string Role { get; set; }

    [MinLength(1)]
    public required List<ResponseInputContentPartRequest> Content { get; set; }
}

public class ResponseInputContentPartRequest
{
    [OpenApiStringEnum("input_text")]
    public required string Type { get; set; }

    [OpenApiDescription("Must be a non-empty string.")]
    public required string Text { get; set; }
}

public class ResponseFormatRequest
{
    [OpenApiStringEnum(RequestedResponseFormats.Text, RequestedResponseFormats.JsonObject)]
    public required string Type { get; set; }
}
