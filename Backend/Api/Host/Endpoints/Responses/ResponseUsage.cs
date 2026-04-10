using System.Text.Json.Serialization;

namespace GenProxy.Api.Host.Endpoints;

public record ResponseUsage(
    [property: JsonPropertyName("input_tokens")] int InputTokens,
    [property: JsonPropertyName("output_tokens")] int? OutputTokens,
    [property: JsonPropertyName("total_tokens")] int? TotalTokens);
