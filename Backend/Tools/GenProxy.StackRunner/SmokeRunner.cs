using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Authentication;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GenProxy.StackRunner;

internal enum StackRunnerMode
{
    StackRun,
    Smoke
}

internal enum SmokeSuite
{
    Basic,
    Budget,
    All
}

internal static class SmokeSuiteUtilities
{
    public static IReadOnlyList<SmokeSuite> Expand(SmokeSuite suite) => suite switch
    {
        SmokeSuite.All => [SmokeSuite.Basic, SmokeSuite.Budget],
        _ => [suite]
    };

    public static int? GetEffectiveMainContextSize(
        StackRunnerMode mode,
        SmokeSuite suite,
        int? configuredMainContextSize,
        int? smokeMainContextSize)
    {
        if (mode == StackRunnerMode.Smoke && suite == SmokeSuite.Budget && smokeMainContextSize is not null)
        {
            return smokeMainContextSize;
        }

        return configuredMainContextSize;
    }

    public static int? GetEffectiveSummarizerContextSize(
        StackRunnerMode mode,
        SmokeSuite suite,
        int? configuredSummarizerContextSize,
        int? smokeSummarizerContextSize)
    {
        if (mode == StackRunnerMode.Smoke && suite == SmokeSuite.Budget && smokeSummarizerContextSize is not null)
        {
            return smokeSummarizerContextSize;
        }

        return configuredSummarizerContextSize;
    }
}

internal sealed class SmokeRunner(StackRunnerOptions options) : IDisposable
{
    private readonly StackRunnerOptions _options = options;
    private readonly HttpClient _httpClient = CreateHttpClient(options);
    private readonly Uri _apiBaseUri = new(options.ApiBaseUrl);
    private readonly Uri _responsesUri = new($"{options.ApiBaseUrl.TrimEnd('/')}/v1/responses");

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        ConsoleStyling.Info($">>> Running {_options.SmokeSuite.ToString().ToLowerInvariant()} smoke suite");

        foreach (var scenario in CreateScenarios(_options.SmokeSuite, _options.MainModelId))
        {
            ConsoleStyling.Info($">>> Smoke: {scenario.Name}");
            using var request = new HttpRequestMessage(HttpMethod.Post, _responsesUri)
            {
                Content = JsonContent.Create(scenario.Request)
            };
            if (scenario.IncludeApiKey)
            {
                request.Headers.Add("X-API-Key", _options.PublicApiKey);
            }
            using var response = await SendAsync(request, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var error = response.StatusCode != scenario.ExpectedStatusCode
                ? $"Expected HTTP {(int)scenario.ExpectedStatusCode}, got {(int)response.StatusCode}."
                : ValidateResponseBody(scenario, responseBody);
            if (error is not null)
            {
                ConsoleStyling.Error($"Smoke scenario '{scenario.Name}' failed: {error}");
                if (!string.IsNullOrWhiteSpace(responseBody))
                {
                    ConsoleStyling.Error(Truncate(responseBody, 800));
                }
                return 1;
            }
            ConsoleStyling.Success($"PASS {scenario.Name} -> HTTP {(int)response.StatusCode}");
        }

        ConsoleStyling.Success(">>> Smoke checks passed");
        return 0;
    }

    public void Dispose() => _httpClient.Dispose();

    internal static IReadOnlyList<SmokeScenario> CreateScenarios(SmokeSuite suite, string modelId) => suite switch
    {
        SmokeSuite.Basic =>
        [
            new SmokeScenario(
                "authorized response generation",
                CreateRequest(modelId, "smoke ping"),
                HttpStatusCode.OK,
                ResponseValidation: SmokeResponseValidation.ResponseEnvelope),
            new SmokeScenario(
                "json schema response generation",
                CreateRequest(
                    modelId,
                    "Return a JSON object with one boolean field named ok.",
                    responseFormat: SmokeResponseFormatPayload.CreateJsonSchema()),
                HttpStatusCode.OK,
                ResponseValidation: SmokeResponseValidation.StructuredJsonOutputText),
            new SmokeScenario(
                "unauthorized request is rejected",
                CreateRequest(modelId, "missing key"),
                HttpStatusCode.Unauthorized,
                IncludeApiKey: false),
            new SmokeScenario(
                "invalid request is rejected",
                CreateRequest(string.Empty, string.Empty),
                HttpStatusCode.BadRequest)
        ],
        SmokeSuite.Budget =>
        [
            new SmokeScenario(
                "oversized prompt is rejected after reduction",
                CreateRequest(modelId, GenerateOversizedPrompt()),
                HttpStatusCode.UnprocessableEntity)
        ],
        SmokeSuite.All => throw new InvalidOperationException("Smoke suite 'All' must be expanded before scenario execution."),
        _ => throw new ArgumentOutOfRangeException(nameof(suite), suite, "Unsupported smoke suite.")
    };

    internal static string? ValidateResponseBody(SmokeScenario scenario, string responseBody) =>
        scenario.ResponseValidation switch
        {
            SmokeResponseValidation.None => null,
            SmokeResponseValidation.ResponseEnvelope => ValidateEnvelope(responseBody),
            SmokeResponseValidation.StructuredJsonOutputText => ValidateStructuredJsonOutputText(responseBody),
            _ => $"Unsupported smoke response validation '{scenario.ResponseValidation}'."
        };

    internal static string? ValidateStructuredJsonOutputText(string responseBody)
    {
        var envelopeError = ValidateEnvelope(responseBody);
        if (envelopeError is not null)
        {
            return envelopeError;
        }
        try
        {
            using var responseDocument = JsonDocument.Parse(responseBody);
            if (responseDocument.RootElement.ValueKind != JsonValueKind.Object)
            {
                return "response body must be a JSON object.";
            }

            if (!responseDocument.RootElement.TryGetProperty("output_text", out var outputTextElement) ||
                outputTextElement.ValueKind != JsonValueKind.String)
            {
                return "response body must contain a string output_text field.";
            }

            var outputText = outputTextElement.GetString();
            if (string.IsNullOrWhiteSpace(outputText))
            {
                return "output_text must contain a JSON object.";
            }

            using var outputDocument = JsonDocument.Parse(outputText);
            var output = outputDocument.RootElement;
            return output.ValueKind == JsonValueKind.Object && output.EnumerateObject().Count() == 1 &&
                output.TryGetProperty("ok", out var ok) && ok.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? null
                : "output_text must contain exactly one boolean property named ok.";
        }
        catch (JsonException)
        {
            return "output_text must parse as a JSON object.";
        }
    }

    internal static string? ValidateEnvelope(string responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !HasText(root, "id") || !HasText(root, "model") ||
                !HasValue(root, "object", "response") || !HasValue(root, "status", "completed") ||
                !root.TryGetProperty("created_at", out var created) || !created.TryGetInt64(out var timestamp) || timestamp <= 0 ||
                !HasText(root, "output_text"))
            {
                return "Response requires identifiers, model, timestamp, completed status, and nonblank output_text.";
            }
            if (!root.TryGetProperty("output", out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() != 1)
            {
                return "Response requires one assistant output message.";
            }
            var item = items[0];
            if (item.ValueKind != JsonValueKind.Object || !HasText(item, "id") || !HasValue(item, "type", "message") ||
                !HasValue(item, "status", "completed") || !HasValue(item, "role", "assistant") ||
                !item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array || content.GetArrayLength() != 1 ||
                content[0].ValueKind != JsonValueKind.Object || !HasValue(content[0], "type", "output_text") ||
                !HasValue(content[0], "text", root.GetProperty("output_text").GetString()!))
            {
                return "Assistant message content must agree with output_text.";
            }
            if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object ||
                !usage.TryGetProperty("input_tokens", out var input) || !input.TryGetInt32(out var inputTokens) || inputTokens < 0 ||
                !OptionalTokenCount(usage, "output_tokens") || !OptionalTokenCount(usage, "total_tokens"))
            {
                return "Response requires nonnegative token usage, with nullable output and total counts.";
            }
            return null;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return "Response must be a valid JSON response envelope.";
        }
    }

    private static bool HasText(JsonElement element, string name) => element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString());

    private static bool HasValue(JsonElement element, string name, string expected) => element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String && value.GetString() == expected;

    private static bool OptionalTokenCount(JsonElement usage, string name) => usage.TryGetProperty(name, out var value) &&
        (value.ValueKind == JsonValueKind.Null || value.TryGetInt32(out var count) && count >= 0);

    private static HttpClient CreateHttpClient(StackRunnerOptions options)
    {
        HttpMessageHandler handler = CreateHttpMessageHandler(options);

        return new HttpClient(handler)
        {
            Timeout = options.SmokeRequestTimeout
        };
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await _httpClient.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex) when (IsCertificateTrustFailure(ex))
        {
            throw new HttpRequestException(
                $"HTTPS smoke request to '{_apiBaseUri}' failed because the local development certificate is not trusted. " +
                "Smoke should automatically allow loopback dev certificates only for local HTTPS targets; " +
                "if this target is not local, use HTTP for smoke or trust the certificate for manual access.",
                ex);
        }
        catch (HttpRequestException ex) when (_apiBaseUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new HttpRequestException(
                $"HTTPS smoke request to '{_apiBaseUri}' failed after the API started. This is not the local dev-certificate trust case; " +
                "check API availability, URL configuration, and TLS endpoint health.",
                ex);
        }
    }

    private static HttpMessageHandler CreateHttpMessageHandler(StackRunnerOptions options)
    {
        var handler = new HttpClientHandler();
        var apiBaseUri = new Uri(options.ApiBaseUrl);

        if (ShouldAllowLocalDevCertificate(apiBaseUri))
        {
            handler.ServerCertificateCustomValidationCallback = (_, _, _, errors) =>
                errors == SslPolicyErrors.None || errors == SslPolicyErrors.RemoteCertificateChainErrors;
        }

        return handler;
    }

    private static bool ShouldAllowLocalDevCertificate(Uri apiBaseUri)
    {
        if (!apiBaseUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return apiBaseUri.IsLoopback ||
            string.Equals(apiBaseUri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCertificateTrustFailure(HttpRequestException exception)
    {
        return exception.InnerException is AuthenticationException authenticationException &&
            authenticationException.Message.Contains("UntrustedRoot", StringComparison.OrdinalIgnoreCase);
    }

    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength] + "...";
    }

    private static string GenerateOversizedPrompt()
    {
        var repeatedBlock = string.Concat(Enumerable.Repeat("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789", 512));
        return string.Join(
            Environment.NewLine,
            [
                "Return the literal block below exactly as written.",
                "Do not summarize, omit, compress, rename, reorder, or paraphrase any character.",
                "BEGIN_LITERAL_BLOCK",
                repeatedBlock,
                "END_LITERAL_BLOCK"
            ]);
    }

    private static SmokeRequestPayload CreateRequest(string model, string text, SmokeResponseFormatPayload? responseFormat = null)
    {
        return new SmokeRequestPayload(
            model,
            [
                new SmokeInputMessagePayload(
                    "message",
                    "user",
                    [
                        new SmokeInputContentPartPayload("input_text", text)
                    ])
            ],
            responseFormat);
    }
}

internal enum SmokeResponseValidation
{
    None = 0,
    StructuredJsonOutputText = 1,
    ResponseEnvelope = 2
}

internal sealed record SmokeScenario(
    string Name,
    SmokeRequestPayload Request,
    HttpStatusCode ExpectedStatusCode,
    bool IncludeApiKey = true,
    SmokeResponseValidation ResponseValidation = SmokeResponseValidation.None);

internal sealed record SmokeRequestPayload(
    [property: JsonPropertyName("model")]
    string Model,
    [property: JsonPropertyName("input")]
    IReadOnlyList<SmokeInputMessagePayload> Input,
    [property: JsonPropertyName("response_format")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    SmokeResponseFormatPayload? ResponseFormat = null,
    [property: JsonPropertyName("temperature")] double Temperature = 0,
    [property: JsonPropertyName("top_p")] double TopP = 1);

internal sealed record SmokeResponseFormatPayload(
    [property: JsonPropertyName("type")]
    string Type,
    [property: JsonPropertyName("json_schema")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    SmokeJsonSchemaPayload? JsonSchema = null)
{
    public static SmokeResponseFormatPayload CreateJsonSchema()
    {
        return new SmokeResponseFormatPayload(
            "json_schema",
            new SmokeJsonSchemaPayload(
                "smoke_result",
                new
                {
                    type = "object",
                    properties = new
                    {
                        ok = new { type = "boolean" }
                    },
                    required = new[] { "ok" },
                    additionalProperties = false
                },
                true));
    }
}

internal sealed record SmokeJsonSchemaPayload(
    [property: JsonPropertyName("name")]
    string Name,
    [property: JsonPropertyName("schema")]
    object Schema,
    [property: JsonPropertyName("strict")]
    bool Strict);

internal sealed record SmokeInputMessagePayload(
    [property: JsonPropertyName("type")]
    string Type,
    [property: JsonPropertyName("role")]
    string Role,
    [property: JsonPropertyName("content")]
    IReadOnlyList<SmokeInputContentPartPayload> Content);

internal sealed record SmokeInputContentPartPayload(
    [property: JsonPropertyName("type")]
    string Type,
    [property: JsonPropertyName("text")]
    string Text);
