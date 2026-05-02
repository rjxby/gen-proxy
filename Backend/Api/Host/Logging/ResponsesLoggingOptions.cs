namespace GenProxy.Api.Host.Logging;

public class ResponsesLoggingOptions
{
    public const string SectionName = "ResponsesLogging";

    public bool LogBodies { get; set; } = false;
}
