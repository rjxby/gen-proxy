namespace GenProxy.Api.Services.Contracts.Models;

public enum RequestedResponseFormat
{
    Text = 0,
    JsonSchema = 1,
}

public static class RequestedResponseFormats
{
    public const string Text = "text";
    public const string JsonSchema = "json_schema";

    public static IReadOnlyList<string> SupportedValues { get; } = [Text, JsonSchema];

    public static bool IsSupported(string? value) => value is Text or JsonSchema;

    public static string GetWireName(RequestedResponseFormat value)
    {
        return value switch
        {
            RequestedResponseFormat.Text => Text,
            RequestedResponseFormat.JsonSchema => JsonSchema,
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unsupported response format.")
        };
    }
}
