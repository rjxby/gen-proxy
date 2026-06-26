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
    public async Task ValidateAsync_WhenMetadataValueIsNull_ReturnsError()
    {
        var result = await _validator.ValidateAsync(new ResponseCreateRequest
        {
            Model = "stories15m",
            Input = CreateInput("hello"),
            Metadata = new Dictionary<string, string>
            {
                ["trace_id"] = null!
            }
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
    public async Task ValidateAsync_WhenResponseFormatTypeIsLegacyJsonObject_ReturnsError()
    {
        var result = await _validator.ValidateAsync(new ResponseCreateRequest
        {
            Model = "stories15m",
            Input = CreateInput("hello"),
            ResponseFormat = new ResponseFormatRequest
            {
                Type = "json_object"
            }
        });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.PropertyName == "response_format.type");
    }

    [Fact]
    public async Task ValidateAsync_WhenResponseFormatTypeIsText_ReturnsSuccess()
    {
        var result = await _validator.ValidateAsync(new ResponseCreateRequest
        {
            Model = "stories15m",
            Input = CreateInput("hello"),
            ResponseFormat = new ResponseFormatRequest
            {
                Type = "text"
            }
        });

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateAsync_WhenJsonSchemaResponseFormatIsValid_ReturnsSuccess()
    {
        var result = await _validator.ValidateAsync(new ResponseCreateRequest
        {
            Model = "stories15m",
            Input = CreateInput("hello"),
            ResponseFormat = CreateJsonSchemaResponseFormat()
        });

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateAsync_WhenJsonSchemaResponseFormatIsMissingJsonSchema_ReturnsError()
    {
        var result = await _validator.ValidateAsync(new ResponseCreateRequest
        {
            Model = "stories15m",
            Input = CreateInput("hello"),
            ResponseFormat = new ResponseFormatRequest
            {
                Type = "json_schema"
            }
        });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.PropertyName == "response_format.json_schema");
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad name")]
    [InlineData("bad.name")]
    public async Task ValidateAsync_WhenJsonSchemaNameIsInvalid_ReturnsError(string name)
    {
        var responseFormat = CreateJsonSchemaResponseFormat();
        responseFormat.JsonSchema!.Name = name;

        var result = await _validator.ValidateAsync(new ResponseCreateRequest
        {
            Model = "stories15m",
            Input = CreateInput("hello"),
            ResponseFormat = responseFormat
        });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.PropertyName == "response_format.json_schema.name");
    }

    [Fact]
    public async Task ValidateAsync_WhenJsonSchemaNameIsTooLong_ReturnsError()
    {
        var responseFormat = CreateJsonSchemaResponseFormat();
        responseFormat.JsonSchema!.Name = new string('a', 65);

        var result = await _validator.ValidateAsync(new ResponseCreateRequest
        {
            Model = "stories15m",
            Input = CreateInput("hello"),
            ResponseFormat = responseFormat
        });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.PropertyName == "response_format.json_schema.name");
    }

    [Fact]
    public async Task ValidateAsync_WhenJsonSchemaPayloadIsNotObject_ReturnsError()
    {
        var responseFormat = CreateJsonSchemaResponseFormat();
        responseFormat.JsonSchema!.Schema = JsonSerializer.SerializeToElement(new[] { "not", "object" });

        var result = await _validator.ValidateAsync(new ResponseCreateRequest
        {
            Model = "stories15m",
            Input = CreateInput("hello"),
            ResponseFormat = responseFormat
        });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.PropertyName == "response_format.json_schema.schema");
    }

    [Fact]
    public async Task ValidateAsync_WhenJsonSchemaPayloadIsMissing_ReturnsError()
    {
        var responseFormat = CreateJsonSchemaResponseFormat();
        responseFormat.JsonSchema!.Schema = null;

        var result = await _validator.ValidateAsync(new ResponseCreateRequest
        {
            Model = "stories15m",
            Input = CreateInput("hello"),
            ResponseFormat = responseFormat
        });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.PropertyName == "response_format.json_schema.schema");
    }


    [Fact]
    public async Task ValidateAsync_WhenJsonSchemaStrictIsFalse_ReturnsError()
    {
        var responseFormat = CreateJsonSchemaResponseFormat();
        responseFormat.JsonSchema!.Strict = false;

        var result = await _validator.ValidateAsync(new ResponseCreateRequest
        {
            Model = "stories15m",
            Input = CreateInput("hello"),
            ResponseFormat = responseFormat
        });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.PropertyName == "response_format.json_schema.strict");
    }

    [Fact]
    public async Task ValidateAsync_WhenJsonSchemaStrictIsMissing_ReturnsError()
    {
        var responseFormat = CreateJsonSchemaResponseFormat();
        responseFormat.JsonSchema!.Strict = null;

        var result = await _validator.ValidateAsync(new ResponseCreateRequest
        {
            Model = "stories15m",
            Input = CreateInput("hello"),
            ResponseFormat = responseFormat
        });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.PropertyName == "response_format.json_schema.strict");
    }

    [Fact]
    public async Task ValidateAsync_WhenTextResponseFormatHasJsonSchema_ReturnsError()
    {
        var responseFormat = CreateJsonSchemaResponseFormat();
        responseFormat.Type = "text";

        var result = await _validator.ValidateAsync(new ResponseCreateRequest
        {
            Model = "stories15m",
            Input = CreateInput("hello"),
            ResponseFormat = responseFormat
        });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.PropertyName == "response_format.json_schema");
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

    private static ResponseFormatRequest CreateJsonSchemaResponseFormat()
    {
        return new ResponseFormatRequest
        {
            Type = "json_schema",
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
        };
    }
}
