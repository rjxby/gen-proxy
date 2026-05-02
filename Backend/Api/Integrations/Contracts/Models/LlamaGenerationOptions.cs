namespace GenProxy.Api.Integrations.Contracts.Models;

public record LlamaGenerationOptions(
    LlamaResponseFormatType? ResponseFormat,
    int? MaxOutputTokens,
    float? Temperature,
    float? TopP);
