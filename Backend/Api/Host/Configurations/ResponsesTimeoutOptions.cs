namespace GenProxy.Api.Host.Configurations;

public sealed class ResponsesTimeoutOptions
{
    public const string SectionName = "ResponsesTimeout";
    public const string PolicyName = "responses";

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(180);
}
