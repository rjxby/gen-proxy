namespace GenProxy.Api.Services.Contracts.Models;

public record ResponseCreateCommand(
    string Model,
    string Input,
    int? MaxOutputTokens,
    float? Temperature,
    float? TopP,
    RequestedResponseFormat? ResponseFormat,
    string? JsonSchema);
