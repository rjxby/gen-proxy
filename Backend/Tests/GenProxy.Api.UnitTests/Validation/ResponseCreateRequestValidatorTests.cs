using FluentAssertions;
using GenProxy.Api.Host;
using GenProxy.Api.Host.Endpoints;
using GenProxy.Api.Host.Validation;
using Microsoft.Extensions.Options;
using System.Text.Json;
using Xunit;

namespace GenProxy.Api.UnitTests;

public class ResponseCreateRequestValidatorTests
{
    private readonly ResponseCreateRequestValidator _validator = new(Options.Create(new RequestLimitsOptions()));

    [Fact]
    public async Task ValidateAsync_WhenModelMissing_ReturnsError()
    {
        var result = await _validator.ValidateAsync(new ResponseCreateRequest
        {
            Model = "",
            Input = CreateInput("hello")
        });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(error => error.PropertyName == "model");
    }

    [Fact]
    public async Task ValidateAsync_WhenInputMissing_ReturnsError()
    {
        var result = await _validator.ValidateAsync(new ResponseCreateRequest
        {
            Model = "stories15m",
            Input = null!
        });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(error => error.PropertyName == "input");
    }

    [Fact]
    public async Task ValidateAsync_WhenStructuredMessageInputIsValid_ReturnsSuccess()
    {
        var result = await _validator.ValidateAsync(new ResponseCreateRequest
        {
            Model = "stories15m",
            Input = CreateInput("hello")
        });

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateAsync_WhenStructuredInputIsEmptyArray_ReturnsError()
    {
        var result = await _validator.ValidateAsync(new ResponseCreateRequest
        {
            Model = "stories15m",
            Input = []
        });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.PropertyName == "input");
    }

    [Fact]
    public async Task ValidateAsync_WhenStructuredInputContainsMultipleMessages_ReturnsError()
    {
        var result = await _validator.ValidateAsync(new ResponseCreateRequest
        {
            Model = "stories15m",
            Input =
            [
                CreateMessage("hello"),
                CreateMessage("world")
            ]
        });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.PropertyName == "input");
    }

    [Fact]
    public async Task ValidateAsync_WhenStructuredInputHasUnsupportedMessageType_ReturnsError()
    {
        var result = await _validator.ValidateAsync(new ResponseCreateRequest
        {
            Model = "stories15m",
            Input =
            [
                CreateMessage("hello", type: "tool_call")
            ]
        });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.PropertyName == "input[0].type");
    }

    [Fact]
    public async Task ValidateAsync_WhenStructuredInputRoleIsNotUser_ReturnsError()
    {
        var result = await _validator.ValidateAsync(new ResponseCreateRequest
        {
            Model = "stories15m",
            Input =
            [
                CreateMessage("hello", role: "assistant")
            ]
        });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.PropertyName == "input[0].role");
    }

    [Fact]
    public async Task ValidateAsync_WhenStructuredInputContentIsEmpty_ReturnsError()
    {
        var result = await _validator.ValidateAsync(new ResponseCreateRequest
        {
            Model = "stories15m",
            Input =
            [
                new ResponseInputMessageRequest
                {
                    Type = "message",
                    Role = "user",
                    Content = []
                }
            ]
        });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.PropertyName == "input[0].content");
    }

    [Fact]
    public async Task ValidateAsync_WhenStructuredInputContentTypeIsUnsupported_ReturnsError()
    {
        var result = await _validator.ValidateAsync(new ResponseCreateRequest
        {
            Model = "stories15m",
            Input =
            [
                CreateMessage("hello", contentType: "output_text")
            ]
        });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.PropertyName == "input[0].content[0].type");
    }

    [Fact]
    public async Task ValidateAsync_WhenStructuredInputTextIsMissing_ReturnsError()
    {
        var result = await _validator.ValidateAsync(new ResponseCreateRequest
        {
            Model = "stories15m",
            Input =
            [
                CreateMessage((string)null!)
            ]
        });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.PropertyName == "input[0].content[0].text");
    }

    [Fact]
    public async Task ValidateAsync_WhenStructuredInputTextIsBlank_ReturnsError()
    {
        var result = await _validator.ValidateAsync(new ResponseCreateRequest
        {
            Model = "stories15m",
            Input = CreateInput(" ")
        });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.PropertyName == "input[0].content[0].text");
    }

    [Fact]
    public async Task ValidateAsync_WhenNormalizedPromptExceedsLimit_ReturnsError()
    {
        var validator = new ResponseCreateRequestValidator(Options.Create(new RequestLimitsOptions
        {
            MaxInputCharacters = 10
        }));

        var result = await validator.ValidateAsync(new ResponseCreateRequest
        {
            Model = "stories15m",
            Input = CreateInput("hello", "world!")
        });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.PropertyName == "input");
    }

    [Fact]
    public async Task ValidateAsync_WhenMetadataHasTooManyEntries_ReturnsError()
    {
        var metadata = Enumerable.Range(0, 17)
            .ToDictionary(index => $"key{index}", index => "value");

        var result = await _validator.ValidateAsync(new ResponseCreateRequest
        {
            Model = "stories15m",
            Input = CreateInput("hello"),
            Metadata = metadata
        });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.PropertyName == "metadata");
    }

    [Fact]
    public async Task ValidateAsync_WhenResponseFormatTypeIsUnknown_ReturnsError()
    {
        var result = await _validator.ValidateAsync(new ResponseCreateRequest
        {
            Model = "stories15m",
            Input = CreateInput("hello"),
            ResponseFormat = new ResponseFormatRequest
            {
                Type = "xml"
            }
        });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.PropertyName == "response_format.type");
    }

    [Fact]
    public async Task ValidateAsync_WhenStreamEnabled_ReturnsError()
    {
        var result = await _validator.ValidateAsync(new ResponseCreateRequest
        {
            Model = "stories15m",
            Input = CreateInput("hello"),
            Stream = true
        });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.PropertyName == "stream");
    }

    [Fact]
    public async Task ValidateAsync_WhenToolsProvided_ReturnsError()
    {
        var result = await _validator.ValidateAsync(new ResponseCreateRequest
        {
            Model = "stories15m",
            Input = CreateInput("hello"),
            Tools = JsonSerializer.SerializeToElement(new[] { new { type = "function" } })
        });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.PropertyName == "tools");
    }

    [Fact]
    public async Task ValidateAsync_WhenToolChoiceProvided_ReturnsError()
    {
        var result = await _validator.ValidateAsync(new ResponseCreateRequest
        {
            Model = "stories15m",
            Input = CreateInput("hello"),
            ToolChoice = JsonSerializer.SerializeToElement(new { type = "auto" })
        });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.PropertyName == "tool_choice");
    }

    [Fact]
    public async Task ValidateAsync_WhenTemperatureIsOutOfRange_ReturnsError()
    {
        var result = await _validator.ValidateAsync(new ResponseCreateRequest
        {
            Model = "stories15m",
            Input = CreateInput("hello"),
            Temperature = 3f
        });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.PropertyName == "temperature");
    }

    [Fact]
    public async Task ValidateAsync_WhenMaxOutputTokensIsNotPositive_ReturnsError()
    {
        var result = await _validator.ValidateAsync(new ResponseCreateRequest
        {
            Model = "stories15m",
            Input = CreateInput("hello"),
            MaxOutputTokens = 0
        });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.PropertyName == "max_output_tokens");
    }

    [Fact]
    public async Task ValidateAsync_WhenTopPIsOutOfRange_ReturnsError()
    {
        var result = await _validator.ValidateAsync(new ResponseCreateRequest
        {
            Model = "stories15m",
            Input = CreateInput("hello"),
            TopP = -0.1f
        });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.PropertyName == "top_p");
    }

    private static List<ResponseInputMessageRequest> CreateInput(params string[] texts)
    {
        return
        [
            CreateMessage(texts)
        ];
    }

    private static ResponseInputMessageRequest CreateMessage(
        string text,
        string type = "message",
        string role = "user",
        string contentType = "input_text")
    {
        return CreateMessage([text], type, role, contentType);
    }

    private static ResponseInputMessageRequest CreateMessage(
        string[] texts,
        string type = "message",
        string role = "user",
        string contentType = "input_text")
    {
        return new ResponseInputMessageRequest
        {
            Type = type,
            Role = role,
            Content = texts
                .Select(text => new ResponseInputContentPartRequest
                {
                    Type = contentType,
                    Text = text
                })
                .ToList()
        };
    }
}
