using FluentAssertions;
using GenProxy.Api.Host.Endpoints;
using GenProxy.Api.Services.Contracts.Models;
using System.Text.Json;
using Xunit;

namespace GenProxy.Api.UnitTests;

public class ResponseCreateRequestMapperTests
{
    [Fact]
    public void Map_WhenStructuredInputHasSingleTextPart_ProducesPrompt()
    {
        var result = ResponseCreateRequestMapper.Map(new ResponseCreateRequest
        {
            Model = "stories15m",
            Input = CreateInput("hello")
        });

        result.Input.Should().Be("hello");
    }

    [Fact]
    public void Map_WhenJsonSchemaResponseFormatProvided_MapsToServiceOwnedFormatAndSchema()
    {
        var result = ResponseCreateRequestMapper.Map(
            new ResponseCreateRequest
            {
                Model = "stories15m",
                Input = CreateInput("hello"),
                ResponseFormat = new ResponseFormatRequest
                {
                    Type = RequestedResponseFormats.JsonSchema,
                    JsonSchema = new JsonSchemaResponseFormatRequest
                    {
                        Name = "result",
                        Schema = JsonSerializer.SerializeToElement(new
                        {
                            type = "object",
                            properties = new
                            {
                                ok = new { type = "boolean" }
                            },
                            required = new[] { "ok" },
                            additionalProperties = false
                        }),
                        Strict = true
                    }
                }
            });

        result.ResponseFormat.Should().Be(RequestedResponseFormat.JsonSchema);
        result.JsonSchema.Should().Be("{\"type\":\"object\",\"properties\":{\"ok\":{\"type\":\"boolean\"}},\"required\":[\"ok\"],\"additionalProperties\":false}");
    }

    [Fact]
    public void Map_WhenStructuredInputContainsMultipleTextParts_JoinsThemWithNewLines()
    {
        var result = ResponseCreateRequestMapper.Map(
            new ResponseCreateRequest
            {
                Model = "stories15m",
                Input = CreateInput("hello", "world")
            });

        result.Input.Should().Be("hello\nworld");
    }

    private static List<ResponseInputMessageRequest> CreateInput(params string[] texts)
    {
        return
        [
            new ResponseInputMessageRequest
            {
                Type = "message",
                Role = "user",
                Content = texts
                    .Select(text => new ResponseInputContentPartRequest
                    {
                        Type = "input_text",
                        Text = text
                    })
                    .ToList()
            }
        ];
    }
}
