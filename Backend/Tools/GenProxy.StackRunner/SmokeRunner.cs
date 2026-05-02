using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Authentication;
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

            if (response.StatusCode != scenario.ExpectedStatusCode)
            {
                ConsoleStyling.Error(
                    $"Smoke scenario '{scenario.Name}' failed: expected HTTP {(int)scenario.ExpectedStatusCode}, got {(int)response.StatusCode}.");

                if (!string.IsNullOrWhiteSpace(responseBody))
                {
                    ConsoleStyling.Warning("Response body:");
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
                HttpStatusCode.OK),
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

    private static SmokeRequestPayload CreateRequest(string model, string text)
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
            ]);
    }
}

internal sealed record SmokeScenario(
    string Name,
    SmokeRequestPayload Request,
    HttpStatusCode ExpectedStatusCode,
    bool IncludeApiKey = true);

internal sealed record SmokeRequestPayload(
    [property: JsonPropertyName("model")]
    string Model,
    [property: JsonPropertyName("input")]
    IReadOnlyList<SmokeInputMessagePayload> Input);

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
