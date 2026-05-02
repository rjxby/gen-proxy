using FluentAssertions;
using GenProxy.Api.Integrations.Contracts;
using GenProxy.Api.Integrations.Contracts.Models;
using GenProxy.Api.Integrations.Contracts.Configuration;
using GenProxy.Api.Services.Contracts;
using GenProxy.Api.Services.Implementation.Configuration;
using GenProxy.Api.Services.Implementation.Services;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace GenProxy.Api.UnitTests;

public class LlmPromptSummarizerTests
{
    [Fact]
    public async Task ReduceAsync_UsesReducerRuntimeClientAndReturnsGeneratedPrompt()
    {
        var runtimeClient = new Mock<IPromptReducerRuntimeClient>();
        runtimeClient
            .Setup(client => client.GenerateAsync(
                It.IsAny<string>(),
                It.Is<string>(prompt =>
                    prompt.Contains("Maximum allowed input tokens for this request: 123") &&
                    prompt.Contains("Original user input:\nlong prompt")),
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlamaGenerationResult("reduce_1", "", "short prompt", null, null));

        var summarizer = new LlmPromptSummarizer(
            runtimeClient.Object,
            Options.Create(new PromptReducerRuntimeOptions()),
            Options.Create(new PromptReductionOptions()));

        var result = await summarizer.ReduceAsync("long prompt", 123, CancellationToken.None);

        result.Prompt.Should().Be("short prompt");
        result.WasReduced.Should().BeTrue();
        result.Strategy.Should().Be(PromptReductionStrategy.LlmSummarizer);
    }

    [Fact]
    public async Task ReduceAsync_WhenReducerRuntimeDisabled_DoesNotCallRuntime()
    {
        var runtimeClient = new Mock<IPromptReducerRuntimeClient>(MockBehavior.Strict);

        var summarizer = new LlmPromptSummarizer(
            runtimeClient.Object,
            Options.Create(new PromptReducerRuntimeOptions
            {
                Enabled = false
            }),
            Options.Create(new PromptReductionOptions()));

        var result = await summarizer.ReduceAsync("long prompt", 123, CancellationToken.None);

        result.Prompt.Should().Be("long prompt");
        result.WasReduced.Should().BeFalse();
        result.Strategy.Should().Be(PromptReductionStrategy.None);
        runtimeClient.Verify(
            client => client.GenerateAsync(It.IsAny<string>(), It.IsAny<string>(), null, It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ReduceAsync_WhenRuntimeThrowsUnexpectedError_WrapsAsPromptReductionFailedException()
    {
        var runtimeClient = new Mock<IPromptReducerRuntimeClient>();
        runtimeClient
            .Setup(client => client.GenerateAsync(It.IsAny<string>(), It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("bad reducer response"));

        var summarizer = new LlmPromptSummarizer(
            runtimeClient.Object,
            Options.Create(new PromptReducerRuntimeOptions()),
            Options.Create(new PromptReductionOptions()));

        var act = async () => await summarizer.ReduceAsync("long prompt", 123, CancellationToken.None);

        await act.Should().ThrowAsync<PromptReductionFailedException>();
    }

    [Fact]
    public async Task ReduceAsync_WhenRuntimeThrowsPromptBudgetExceeded_PropagatesException()
    {
        var runtimeClient = new Mock<IPromptReducerRuntimeClient>();
        runtimeClient
            .Setup(client => client.GenerateAsync(It.IsAny<string>(), It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new LlamaRuntimePromptBudgetExceededException("Prompt exceeds input budget: 6250 tokens > 3584 allowed."));

        var summarizer = new LlmPromptSummarizer(
            runtimeClient.Object,
            Options.Create(new PromptReducerRuntimeOptions()),
            Options.Create(new PromptReductionOptions()));

        var act = async () => await summarizer.ReduceAsync("long prompt", 123, CancellationToken.None);

        await act.Should().ThrowAsync<LlamaRuntimePromptBudgetExceededException>();
    }
}
