using FluentAssertions;
using GenProxy.Api.Integrations.Contracts;
using GenProxy.Api.Integrations.Contracts.Models;
using GenProxy.Api.Services.Contracts;
using GenProxy.Api.Services.Implementation.Services;
using Google.Protobuf;
using LlamaRuntime.Presentation.Grpc;
using Moq;
using Xunit;

namespace GenProxy.Api.UnitTests;

public class LeadingPromptTruncatorTests
{
    [Fact]
    public async Task ReduceAsync_TruncatesFromBeginningUntilPromptFits()
    {
        var generationRuntimeClient = new Mock<IGenerationRuntimeClient>();
        generationRuntimeClient
            .SetupSequence(client => client.EstimateTokensAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TokenEstimation(10, 100, 0, 6, false))
            .ReturnsAsync(new TokenEstimation(6, 100, 0, 6, true));

        var reducer = new LeadingPromptTruncator(generationRuntimeClient.Object);

        var result = await reducer.ReduceAsync("abcdefghij", 6, CancellationToken.None);

        result.Prompt.Should().Be("efghij");
        result.WasReduced.Should().BeTrue();
        result.Strategy.Should().Be(PromptReductionStrategy.LeadingTruncation);
    }

    [Fact]
    public async Task ReduceAsync_WhenPromptAlreadyFits_ReturnsOriginalPrompt()
    {
        var generationRuntimeClient = new Mock<IGenerationRuntimeClient>();
        generationRuntimeClient
            .Setup(client => client.EstimateTokensAsync("prompt", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TokenEstimation(5, 100, 0, 10, true));

        var reducer = new LeadingPromptTruncator(generationRuntimeClient.Object);

        var result = await reducer.ReduceAsync("prompt", 10, CancellationToken.None);

        result.Prompt.Should().Be("prompt");
        result.WasReduced.Should().BeFalse();
        result.Strategy.Should().Be(PromptReductionStrategy.None);
    }

    [Theory]
    [InlineData("😀x", 3, 2, "x")]
    [InlineData("a😀x", 4, 2, "x")]
    [InlineData("😀x", 3, 1, "x")]
    [InlineData("a😀x", 4, 3, "😀x")]
    [InlineData("😀", 2, 1, "")]
    public async Task ReduceAsync_WhenTruncatingUnicode_PreservesScalarsThroughRuntimeSerialization(
        string prompt, int tokenCount, int maxAllowedInputTokens, string expectedPrompt)
    {
        var generationRuntimeClient = new Mock<IGenerationRuntimeClient>(MockBehavior.Strict);
        generationRuntimeClient
            .Setup(client => client.EstimateTokensAsync(prompt, CancellationToken.None))
            .ReturnsAsync(new TokenEstimation(tokenCount, 100, 0, maxAllowedInputTokens, false));
        if (expectedPrompt.Length > 0)
        {
            generationRuntimeClient
                .Setup(client => client.EstimateTokensAsync(expectedPrompt, CancellationToken.None))
                .ReturnsAsync(new TokenEstimation(1, 100, 0, maxAllowedInputTokens, true));
        }

        var reducer = new LeadingPromptTruncator(generationRuntimeClient.Object);

        var result = await reducer.ReduceAsync(prompt, maxAllowedInputTokens, CancellationToken.None);

        result.Prompt.Should().Be(expectedPrompt);
        result.WasReduced.Should().BeTrue();
        result.Strategy.Should().Be(PromptReductionStrategy.LeadingTruncation);
        var runtimeRequest = new EstimateTokensRequest { Prompt = result.Prompt };
        EstimateTokensRequest.Parser.ParseFrom(runtimeRequest.ToByteArray()).Prompt.Should().Be(expectedPrompt);
        generationRuntimeClient.Verify(client => client.EstimateTokensAsync(prompt, CancellationToken.None), Times.Once);
        if (expectedPrompt.Length > 0)
        {
            generationRuntimeClient.Verify(client => client.EstimateTokensAsync(expectedPrompt, CancellationToken.None), Times.Once);
        }
        generationRuntimeClient.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ReduceAsync_WhenRepeatedTruncationSplitsSurrogatePairs_ReestimatesEachValidSuffix()
    {
        var generationRuntimeClient = new Mock<IGenerationRuntimeClient>(MockBehavior.Strict);
        generationRuntimeClient
            .Setup(client => client.EstimateTokensAsync("😀😀x", CancellationToken.None))
            .ReturnsAsync(new TokenEstimation(9, 100, 0, 6, false));
        generationRuntimeClient
            .Setup(client => client.EstimateTokensAsync("😀x", CancellationToken.None))
            .ReturnsAsync(new TokenEstimation(7, 100, 0, 6, false));
        generationRuntimeClient
            .Setup(client => client.EstimateTokensAsync("x", CancellationToken.None))
            .ReturnsAsync(new TokenEstimation(1, 100, 0, 6, true));

        var reducer = new LeadingPromptTruncator(generationRuntimeClient.Object);

        var result = await reducer.ReduceAsync("😀😀x", 6, CancellationToken.None);

        result.Prompt.Should().Be("x");
        result.WasReduced.Should().BeTrue();
        result.Strategy.Should().Be(PromptReductionStrategy.LeadingTruncation);
        generationRuntimeClient.Verify(client => client.EstimateTokensAsync("😀😀x", CancellationToken.None), Times.Once);
        generationRuntimeClient.Verify(client => client.EstimateTokensAsync("😀x", CancellationToken.None), Times.Once);
        generationRuntimeClient.Verify(client => client.EstimateTokensAsync("x", CancellationToken.None), Times.Once);
        generationRuntimeClient.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ReduceAsync_WhenPromptIsEmpty_SkipsEstimation()
    {
        var generationRuntimeClient = new Mock<IGenerationRuntimeClient>(MockBehavior.Strict);
        var reducer = new LeadingPromptTruncator(generationRuntimeClient.Object);

        var result = await reducer.ReduceAsync(string.Empty, 1, CancellationToken.None);

        result.Prompt.Should().BeEmpty();
        result.WasReduced.Should().BeFalse();
        result.Strategy.Should().Be(PromptReductionStrategy.None);
        generationRuntimeClient.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ReduceAsync_WhenEstimationIsCanceled_PropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var generationRuntimeClient = new Mock<IGenerationRuntimeClient>(MockBehavior.Strict);
        generationRuntimeClient
            .Setup(client => client.EstimateTokensAsync("😀x", cancellation.Token))
            .Returns(Task.FromCanceled<TokenEstimation>(cancellation.Token));
        var reducer = new LeadingPromptTruncator(generationRuntimeClient.Object);

        Func<Task> act = () => reducer.ReduceAsync("😀x", 2, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        generationRuntimeClient.Verify(client => client.EstimateTokensAsync("😀x", cancellation.Token), Times.Once);
        generationRuntimeClient.VerifyNoOtherCalls();
    }
}
