using FluentAssertions;
using GenProxy.Api.Integrations.Contracts;
using GenProxy.Api.Integrations.Contracts.Models;
using GenProxy.Api.Services.Contracts;
using GenProxy.Api.Services.Contracts.Models;
using GenProxy.Api.Services.Implementation.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace GenProxy.Api.UnitTests;

public class ResponseGenerationServiceTests
{
    private readonly Mock<IGenerationRuntimeClient> _generationRuntimeClient;
    private readonly Mock<IPromptReductionPipeline> _promptReductionPipeline;
    private readonly ResponseGenerationService _service;

    public ResponseGenerationServiceTests()
    {
        _generationRuntimeClient = new Mock<IGenerationRuntimeClient>();
        _promptReductionPipeline = new Mock<IPromptReductionPipeline>();
        _service = new ResponseGenerationService(
            _generationRuntimeClient.Object,
            _promptReductionPipeline.Object,
            NullLogger<ResponseGenerationService>.Instance);
    }

    [Fact]
    public async Task GenerateAsync_WhenPromptFits_GeneratesWithoutReduction()
    {
        var command = new ResponseCreateCommand("gpt-5.1", "hello", 128, null);
        _generationRuntimeClient
            .Setup(client => client.EstimateTokensAsync(command.Input, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TokenEstimation(5, 4096, 256, 3840, true));
        _generationRuntimeClient
            .Setup(client => client.GenerateAsync(It.IsAny<string>(), command.Input, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlamaGenerationResult("resp_123", "world"));

        var result = await _service.GenerateAsync(command, CancellationToken.None);

        result.ResponseId.Should().Be("resp_123");
        result.OutputText.Should().Be("world");
        result.WasReduced.Should().BeFalse();
        result.ReductionStrategy.Should().Be("none");
        _promptReductionPipeline.Verify(
            pipeline => pipeline.ReduceAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GenerateAsync_WhenPromptDoesNotFit_ReducesThenGenerates()
    {
        var command = new ResponseCreateCommand("gpt-5.1", "very long prompt", 128, null);
        _generationRuntimeClient
            .SetupSequence(client => client.EstimateTokensAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TokenEstimation(9000, 8192, 512, 7680, false))
            .ReturnsAsync(new TokenEstimation(7000, 8192, 512, 7680, true));
        _promptReductionPipeline
            .Setup(pipeline => pipeline.ReduceAsync(command.Input, 7680, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PromptReductionResult("compressed prompt", true, "llm_summarizer"));
        _generationRuntimeClient
            .Setup(client => client.GenerateAsync(It.IsAny<string>(), "compressed prompt", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlamaGenerationResult("resp_456", "done"));

        var result = await _service.GenerateAsync(command, CancellationToken.None);

        result.WasReduced.Should().BeTrue();
        result.FinalPrompt.Should().Be("compressed prompt");
        result.OutputText.Should().Be("done");
        result.ReductionStrategy.Should().Be("llm_summarizer");
        _promptReductionPipeline.Verify(
            pipeline => pipeline.ReduceAsync(command.Input, 7680, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GenerateAsync_WhenPromptStillDoesNotFit_ThrowsPromptBudgetExceededException()
    {
        var command = new ResponseCreateCommand("gpt-5.1", "too long", null, null);
        _generationRuntimeClient
            .SetupSequence(client => client.EstimateTokensAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TokenEstimation(9000, 8192, 512, 7680, false))
            .ReturnsAsync(new TokenEstimation(8000, 8192, 512, 7680, false));
        _promptReductionPipeline
            .Setup(pipeline => pipeline.ReduceAsync(command.Input, 7680, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PromptReductionResult("still too long", true, "llm_summarizer"));

        var act = async () => await _service.GenerateAsync(command, CancellationToken.None);

        await act.Should().ThrowAsync<PromptBudgetExceededException>();
    }
}
