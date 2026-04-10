using System.Text.Json.Serialization;

namespace GenProxy.Api.Host.Endpoints;

public class ResponseCreateRequest
{
    public required string Model { get; set; }

    public required string Input { get; set; }

    [JsonPropertyName("max_output_tokens")]
    public int? MaxOutputTokens { get; set; }

    public Dictionary<string, string>? Metadata { get; set; }
}
