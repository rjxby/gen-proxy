using FluentAssertions;
using GenProxy.Api.Integrations.Contracts;
using GenProxy.Api.Integrations.Contracts.Models;
using GenProxy.Api.Services.Implementation.Services;
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
        result.Strategy.Should().Be("leading_truncation");
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
        result.Strategy.Should().Be("none");
    }
}
