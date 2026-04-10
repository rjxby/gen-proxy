namespace GenProxy.Api.Host.Security;

public class ApiKeyOptions
{
    public const string SectionName = "ApiKeys";

    public List<string> Keys { get; set; } = [];

    public bool AllowUnauthenticatedInDevelopment { get; set; }
}
