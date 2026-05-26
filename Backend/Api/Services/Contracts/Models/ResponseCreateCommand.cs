namespace GenProxy.Api.Services.Contracts.Models;

public record ResponseCreateCommand(
    string Model,
    string Input,
    int? MaxOutputTokens,
    float? Temperature,
    float? TopP,
    IReadOnlyDictionary<string, string>? Metadata,
    RequestedResponseFormat? ResponseFormat,
    string? JsonSchema);
