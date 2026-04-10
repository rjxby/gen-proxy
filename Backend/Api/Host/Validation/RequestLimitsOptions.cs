namespace GenProxy.Api.Host.Validation;

public class RequestLimitsOptions
{
    public const string SectionName = "RequestLimits";

    public int MaxRequestBodyBytes { get; set; } = 131072;

    public int MaxModelCharacters { get; set; } = 128;

    public int MaxInputCharacters { get; set; } = 65536;

    public int MaxMetadataEntries { get; set; } = 16;

    public int MaxMetadataKeyCharacters { get; set; } = 64;

    public int MaxMetadataValueCharacters { get; set; } = 512;
}
