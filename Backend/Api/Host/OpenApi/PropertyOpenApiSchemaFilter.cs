using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace GenProxy.Api.Host.OpenApi;

public sealed class PropertyOpenApiSchemaFilter : ISchemaFilter
{
    public void Apply(IOpenApiSchema schema, SchemaFilterContext context)
    {
        if (schema.Properties is null || schema.Properties.Count == 0)
        {
            return;
        }

        foreach (var property in context.Type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            var propertyName = GetJsonPropertyName(property);
            if (!schema.Properties.TryGetValue(propertyName, out var propertySchema))
            {
                continue;
            }

            var stringEnum = property.GetCustomAttribute<OpenApiStringEnumAttribute>();
            if (stringEnum is not null)
            {
                schema.Properties[propertyName] = CreateStringEnumSchema(stringEnum.Values);
                propertySchema = schema.Properties[propertyName];
            }

            var description = property.GetCustomAttribute<OpenApiDescriptionAttribute>();
            if (description is null)
            {
                continue;
            }

            if (propertySchema is OpenApiSchema openApiSchema)
            {
                openApiSchema.Description = description.Description;
                continue;
            }

            schema.Properties[propertyName] = new OpenApiSchema
            {
                Description = description.Description,
                AllOf = [propertySchema]
            };
        }
    }

    private static string GetJsonPropertyName(PropertyInfo property)
    {
        return property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
            ?? JsonNamingPolicy.CamelCase.ConvertName(property.Name);
    }

    private static IOpenApiSchema CreateStringEnumSchema(IEnumerable<string> values)
    {
        return new OpenApiSchema
        {
            Type = JsonSchemaType.String,
            Enum = values
                .Select(value => (JsonNode)JsonValue.Create(value)!)
                .ToList()
        };
    }
}
