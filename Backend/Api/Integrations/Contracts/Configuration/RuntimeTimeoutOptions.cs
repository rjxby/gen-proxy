namespace GenProxy.Api.Integrations.Contracts.Configuration;

public sealed class RuntimeTimeoutOptions
{
    public TimeSpan EstimateTokens { get; set; } = TimeSpan.FromSeconds(10);

    public TimeSpan GetCapabilities { get; set; } = TimeSpan.FromSeconds(10);

    public TimeSpan Generate { get; set; } = TimeSpan.FromSeconds(120);

    public bool IsValid() => IsValidTimeout(EstimateTokens) && IsValidTimeout(GetCapabilities) && IsValidTimeout(Generate);

    public static bool IsValidTimeout(TimeSpan timeout) => timeout > TimeSpan.Zero && timeout <= TimeSpan.FromDays(1);
}
