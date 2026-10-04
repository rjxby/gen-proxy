using System.Text.Json;
using Xunit;

namespace GenProxy.StackRunner.Tests;

public class SmokeValidationTests
{
    internal static string Envelope(string output) => JsonSerializer.Serialize(new
    {
        id = "resp_test", @object = "response", created_at = 1, status = "completed", model = "test",
        output = new[] { new { id = "msg_test", type = "message", status = "completed", role = "assistant",
            content = new[] { new { type = "output_text", text = output } } } },
        output_text = output, usage = new { input_tokens = 10, output_tokens = 5, total_tokens = 15 }
    });

    [Fact]
    public void Generation_RejectsBlankOutputAndInconsistentMessage()
    {
        Assert.NotNull(SmokeRunner.ValidateEnvelope(Envelope("  ")));
        Assert.NotNull(SmokeRunner.ValidateEnvelope(Envelope("hello").Replace("\"text\":\"hello\"", "\"text\":\"different\"")));
        Assert.Null(SmokeRunner.ValidateEnvelope(Envelope("hello")));
        Assert.NotNull(SmokeRunner.ValidateEnvelope("{\"output_text\":\"hello\"}"));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"ok\":\"true\"}")]
    [InlineData("{\"ok\":true,\"extra\":1}")]
    [InlineData("{\"ok\":true,\"ok\":false}")]
    public void StructuredOutput_RejectsObjectsThatViolateRequestedSchema(string output)
    {
        Assert.NotNull(SmokeRunner.ValidateStructuredJsonOutputText(Envelope(output)));
    }

    [Theory]
    [InlineData("{\"ok\":true}")]
    [InlineData("{\"ok\":false}")]
    public void StructuredOutput_AcceptsRequestedBoolean(string output)
    {
        Assert.Null(SmokeRunner.ValidateStructuredJsonOutputText(Envelope(output)));
    }
}
