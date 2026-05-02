namespace GenProxy.Api.Services.Contracts.Models;

public enum RequestedResponseFormat
{
    Text = 0,
    JsonObject = 1,
}

public static class RequestedResponseFormats
{
    public const string Text = "text";
    public const string JsonObject = "json_object";

    public static IReadOnlyList<string> SupportedValues { get; } = [Text, JsonObject];

    public static bool IsSupported(string? value) => value is Text or JsonObject;

    public static string GetWireName(RequestedResponseFormat value)
    {
        return value switch
        {
            RequestedResponseFormat.Text => Text,
            RequestedResponseFormat.JsonObject => JsonObject,
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unsupported response format.")
        };
    }
}
