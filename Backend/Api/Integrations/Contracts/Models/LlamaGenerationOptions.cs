namespace GenProxy.Api.Integrations.Contracts.Models;

public record LlamaGenerationOptions(
    LlamaResponseFormatType? ResponseFormat,
    string? JsonSchema,
    int? MaxOutputTokens,
    float? Temperature,
    float? TopP);
