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
        var command = new ResponseCreateCommand("stories15m", "hello", 128, 0.4f, 0.9f, null, null);
        _generationRuntimeClient
            .Setup(client => client.EstimateTokensAsync(command.Input, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TokenEstimation(5, 4096, 256, 3840, true));
        _generationRuntimeClient
            .Setup(client => client.GenerateAsync(
                It.IsAny<string>(),
                command.Input,
                It.Is<LlamaGenerationOptions?>(options =>
                    options != null &&
                    options.MaxOutputTokens == 128 &&
                    options.Temperature == 0.4f &&
                    options.TopP == 0.9f &&
                    options.ResponseFormat == null),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlamaGenerationResult(
                "resp_123",
                "runtime-model",
                "world",
                new LlamaUsage(6, 2, 8),
                null));

        var result = await _service.GenerateAsync(command, CancellationToken.None);

        result.ResponseId.Should().Be("resp_123");
        result.Model.Should().Be("runtime-model");
        result.OutputText.Should().Be("world");
        result.InputTokens.Should().Be(6);
        result.OutputTokens.Should().Be(2);
        result.TotalTokens.Should().Be(8);
        result.WasReduced.Should().BeFalse();
        result.ReductionStrategy.Should().Be(PromptReductionStrategy.None);
        _promptReductionPipeline.Verify(
            pipeline => pipeline.ReduceAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _generationRuntimeClient.Verify(
            client => client.GetCapabilitiesAsync(It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GenerateAsync_WhenPromptDoesNotFit_ReducesThenGenerates()
    {
        var command = new ResponseCreateCommand("stories15m", "very long prompt", 128, null, null, null, null);
        _generationRuntimeClient
            .SetupSequence(client => client.EstimateTokensAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TokenEstimation(9000, 8192, 512, 7680, false))
            .ReturnsAsync(new TokenEstimation(7000, 8192, 512, 7680, true));
        _promptReductionPipeline
            .Setup(pipeline => pipeline.ReduceAsync(command.Input, 7680, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PromptReductionResult("compressed prompt", true, PromptReductionStrategy.LlmSummarizer));
        _generationRuntimeClient
            .Setup(client => client.GenerateAsync(
                It.IsAny<string>(),
                "compressed prompt",
                It.Is<LlamaGenerationOptions?>(options =>
                    options != null &&
                    options.MaxOutputTokens == 128 &&
                    options.ResponseFormat == null),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlamaGenerationResult("resp_456", "", "done", null, null));

        var result = await _service.GenerateAsync(command, CancellationToken.None);

        result.WasReduced.Should().BeTrue();
        result.FinalPrompt.Should().Be("compressed prompt");
        result.OutputText.Should().Be("done");
        result.ReductionStrategy.Should().Be(PromptReductionStrategy.LlmSummarizer);
        result.Model.Should().Be("stories15m");
        _promptReductionPipeline.Verify(
            pipeline => pipeline.ReduceAsync(command.Input, 7680, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GenerateAsync_WhenPromptStillDoesNotFit_ThrowsPromptBudgetExceededException()
    {
        var command = new ResponseCreateCommand("stories15m", "too long", null, null, null, null, null);
        _generationRuntimeClient
            .SetupSequence(client => client.EstimateTokensAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TokenEstimation(9000, 8192, 512, 7680, false))
            .ReturnsAsync(new TokenEstimation(8000, 8192, 512, 7680, false));
        _promptReductionPipeline
            .Setup(pipeline => pipeline.ReduceAsync(command.Input, 7680, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PromptReductionResult("still too long", true, PromptReductionStrategy.LlmSummarizer));

        var act = async () => await _service.GenerateAsync(command, CancellationToken.None);

        await act.Should().ThrowAsync<PromptBudgetExceededException>();
    }

    [Fact]
    public async Task GenerateAsync_WhenJsonObjectRequestedAndRuntimeDoesNotSupportIt_ThrowsResponseFormatNotSupportedException()
    {
        var command = new ResponseCreateCommand("stories15m", "{}", 128, null, null, null, RequestedResponseFormat.JsonObject);
        _generationRuntimeClient
            .Setup(client => client.GetCapabilitiesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlamaCapabilities("runtime-model", 8192, true, false, false, "llama"));

        var act = async () => await _service.GenerateAsync(command, CancellationToken.None);

        await act.Should().ThrowAsync<ResponseFormatNotSupportedException>();
        _generationRuntimeClient.Verify(
            client => client.EstimateTokensAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GenerateAsync_WhenJsonObjectRequestedAndRuntimeTraceRejectsIt_ThrowsStructuredOutputNotSatisfiedException()
    {
        var command = new ResponseCreateCommand("stories15m", "{}", 128, null, null, null, RequestedResponseFormat.JsonObject);
        _generationRuntimeClient
            .Setup(client => client.GetCapabilitiesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlamaCapabilities("runtime-model", 8192, true, true, false, "llama"));
        _generationRuntimeClient
            .Setup(client => client.EstimateTokensAsync(command.Input, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TokenEstimation(5, 4096, 256, 3840, true));
        _generationRuntimeClient
            .Setup(client => client.GenerateAsync(
                It.IsAny<string>(),
                command.Input,
                It.Is<LlamaGenerationOptions?>(options =>
                    options!.ResponseFormat == LlamaResponseFormatType.JsonObject &&
                    options.MaxOutputTokens == 128),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlamaGenerationResult(
                "resp_123",
                "runtime-model",
                "{}",
                new LlamaUsage(4, 3, 7),
                new LlamaRuntimeTrace(true, false, false)));

        var act = async () => await _service.GenerateAsync(command, CancellationToken.None);

        await act.Should().ThrowAsync<StructuredOutputNotSatisfiedException>();
    }

    [Fact]
    public async Task GenerateAsync_WhenJsonObjectRequestedAndRuntimeTraceIsMissing_ThrowsStructuredOutputNotSatisfiedException()
    {
        var command = new ResponseCreateCommand("stories15m", "{}", 128, null, null, null, RequestedResponseFormat.JsonObject);
        _generationRuntimeClient
            .Setup(client => client.GetCapabilitiesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlamaCapabilities("runtime-model", 8192, true, true, false, "llama"));
        _generationRuntimeClient
            .Setup(client => client.EstimateTokensAsync(command.Input, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TokenEstimation(5, 4096, 256, 3840, true));
        _generationRuntimeClient
            .Setup(client => client.GenerateAsync(
                It.IsAny<string>(),
                command.Input,
                It.IsAny<LlamaGenerationOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlamaGenerationResult(
                "resp_123",
                "runtime-model",
                "{}",
                new LlamaUsage(4, 3, 7),
                null));

        var act = async () => await _service.GenerateAsync(command, CancellationToken.None);

        await act.Should().ThrowAsync<StructuredOutputNotSatisfiedException>();
    }

    [Fact]
    public async Task GenerateAsync_WhenJsonObjectRequestedAndStructuredOutputWasNotApplied_ThrowsStructuredOutputNotSatisfiedException()
    {
        var command = new ResponseCreateCommand("stories15m", "{}", 128, null, null, null, RequestedResponseFormat.JsonObject);
        _generationRuntimeClient
            .Setup(client => client.GetCapabilitiesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlamaCapabilities("runtime-model", 8192, true, true, false, "llama"));
        _generationRuntimeClient
            .Setup(client => client.EstimateTokensAsync(command.Input, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TokenEstimation(5, 4096, 256, 3840, true));
        _generationRuntimeClient
            .Setup(client => client.GenerateAsync(
                It.IsAny<string>(),
                command.Input,
                It.IsAny<LlamaGenerationOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlamaGenerationResult(
                "resp_123",
                "runtime-model",
                "{}",
                new LlamaUsage(4, 3, 7),
                new LlamaRuntimeTrace(false, true, false)));

        var act = async () => await _service.GenerateAsync(command, CancellationToken.None);

        await act.Should().ThrowAsync<StructuredOutputNotSatisfiedException>();
    }

    [Fact]
    public async Task GenerateAsync_WhenJsonObjectRequestedAndContentIsNotValidJson_ThrowsStructuredOutputNotSatisfiedException()
    {
        var command = new ResponseCreateCommand("stories15m", "{}", 128, null, null, null, RequestedResponseFormat.JsonObject);
        _generationRuntimeClient
            .Setup(client => client.GetCapabilitiesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlamaCapabilities("runtime-model", 8192, true, true, false, "llama"));
        _generationRuntimeClient
            .Setup(client => client.EstimateTokensAsync(command.Input, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TokenEstimation(5, 4096, 256, 3840, true));
        _generationRuntimeClient
            .Setup(client => client.GenerateAsync(
                It.IsAny<string>(),
                command.Input,
                It.IsAny<LlamaGenerationOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlamaGenerationResult(
                "resp_123",
                "runtime-model",
                "not-json",
                new LlamaUsage(4, 3, 7),
                new LlamaRuntimeTrace(true, true, false)));

        var act = async () => await _service.GenerateAsync(command, CancellationToken.None);

        await act.Should().ThrowAsync<StructuredOutputNotSatisfiedException>();
    }

    [Fact]
    public async Task GenerateAsync_WhenJsonObjectRequestedAndContentIsNotAnObject_ThrowsStructuredOutputNotSatisfiedException()
    {
        var command = new ResponseCreateCommand("stories15m", "{}", 128, null, null, null, RequestedResponseFormat.JsonObject);
        _generationRuntimeClient
            .Setup(client => client.GetCapabilitiesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlamaCapabilities("runtime-model", 8192, true, true, false, "llama"));
        _generationRuntimeClient
            .Setup(client => client.EstimateTokensAsync(command.Input, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TokenEstimation(5, 4096, 256, 3840, true));
        _generationRuntimeClient
            .Setup(client => client.GenerateAsync(
                It.IsAny<string>(),
                command.Input,
                It.IsAny<LlamaGenerationOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlamaGenerationResult(
                "resp_123",
                "runtime-model",
                "[1,2,3]",
                new LlamaUsage(4, 3, 7),
                new LlamaRuntimeTrace(true, true, false)));

        var act = async () => await _service.GenerateAsync(command, CancellationToken.None);

        await act.Should().ThrowAsync<StructuredOutputNotSatisfiedException>();
    }
}
