using System.Diagnostics.Metrics;

namespace GenProxy.Api.Integrations.Contracts;

public static class GenProxyMetrics
{
    private static readonly Meter Meter = new("GenProxy.Api");

    public static readonly Counter<long> RequestsStarted = Meter.CreateCounter<long>("genproxy.requests.started");
    public static readonly Counter<long> RequestsSucceeded = Meter.CreateCounter<long>("genproxy.requests.succeeded");
    public static readonly Counter<long> RequestsFailed = Meter.CreateCounter<long>("genproxy.requests.failed");
    public static readonly Counter<long> PromptReductions = Meter.CreateCounter<long>("genproxy.prompt_reductions");
    public static readonly Counter<long> UpstreamFailures = Meter.CreateCounter<long>("genproxy.upstream.failures");
    public static readonly Counter<long> AuthenticationFailures = Meter.CreateCounter<long>("genproxy.authentication.failures");
    public static readonly Counter<long> RateLimitRejections = Meter.CreateCounter<long>("genproxy.rate_limit.rejections");
    public static readonly Histogram<double> RequestLatencyMs = Meter.CreateHistogram<double>("genproxy.requests.duration_ms");
    public static readonly Histogram<double> UpstreamLatencyMs = Meter.CreateHistogram<double>("genproxy.upstream.duration_ms");
}
