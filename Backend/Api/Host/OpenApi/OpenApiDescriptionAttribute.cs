namespace GenProxy.Api.Host.OpenApi;

[AttributeUsage(AttributeTargets.Property)]
public sealed class OpenApiDescriptionAttribute(string description) : Attribute
{
    public string Description { get; } = description;
}
