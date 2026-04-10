namespace GenProxy.Api.Services.Contracts.Models;

public record ResponseCreateCommand(
    string Model,
    string Input,
    int? MaxOutputTokens,
    IReadOnlyDictionary<string, string>? Metadata);
