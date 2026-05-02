namespace GenProxy.Api.Host.OpenApi;

[AttributeUsage(AttributeTargets.Property)]
public sealed class OpenApiStringEnumAttribute(params string[] values) : Attribute
{
    public IReadOnlyList<string> Values { get; } = values;
}
