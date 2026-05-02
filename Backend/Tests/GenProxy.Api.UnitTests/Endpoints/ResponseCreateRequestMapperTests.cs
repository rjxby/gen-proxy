using FluentAssertions;
using GenProxy.Api.Host.Endpoints;
using GenProxy.Api.Services.Contracts.Models;
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
    public void Map_WhenResponseFormatProvided_MapsToServiceOwnedEnum()
    {
        var result = ResponseCreateRequestMapper.Map(
            new ResponseCreateRequest
            {
                Model = "stories15m",
                Input = CreateInput("hello"),
                ResponseFormat = new ResponseFormatRequest
                {
                    Type = RequestedResponseFormats.JsonObject
                }
            });

        result.ResponseFormat.Should().Be(RequestedResponseFormat.JsonObject);
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
