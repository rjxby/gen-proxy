using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using GenProxy.Api.Host.Endpoints;
using Xunit;

namespace GenProxy.Api.UnitTests;

public class ResponseEnumJsonSerializationTests
{
    private static readonly JsonSerializerOptions ApiJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public void Serialize_ResponseEnumValue_UsesExactWireName()
    {
        var json = JsonSerializer.Serialize(ResponseContentPartType.OutputText);

        json.Should().Be("\"output_text\"");
    }

    [Fact]
    public void Deserialize_ResponseEnumValue_UsesExactWireName()
    {
        var value = JsonSerializer.Deserialize<ResponseRole>("\"assistant\"");

        value.Should().Be(ResponseRole.Assistant);
    }

    [Fact]
    public void Serialize_ResponsePayload_PreservesStringEnumFields()
    {
        var payload = new ResponsesApiResponse(
            "resp_123",
            ResponseObjectType.Response,
            123,
            ResponseStatus.Completed,
            "runtime-model",
            [
                new ResponseOutputItem(
                    "msg_123",
                    ResponseItemType.Message,
                    ResponseStatus.Completed,
                    ResponseRole.Assistant,
                    [new ResponseContentPart(ResponseContentPartType.OutputText, "hello")])
            ],
            "hello",
            new ResponseUsage(1, 2, 3));

        var json = JsonSerializer.Serialize(payload, ApiJsonOptions);

        json.Should().Contain("\"object\":\"response\"");
        json.Should().Contain("\"status\":\"completed\"");
        json.Should().Contain("\"type\":\"message\"");
        json.Should().Contain("\"role\":\"assistant\"");
        json.Should().Contain("\"type\":\"output_text\"");
    }
}
