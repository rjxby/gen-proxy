using FluentAssertions;
using GenProxy.Api.Integrations.Contracts;
using GenProxy.Api.Integrations.Contracts.Configuration;
using GenProxy.Api.Integrations.Contracts.Models;
using GenProxy.Api.Services.Contracts;
using GenProxy.Api.Services.Contracts.Models;
using GenProxy.Api.Services.Implementation.Configuration;
using GenProxy.Api.Services.Implementation.Services;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace GenProxy.Api.UnitTests;

public class PromptReductionCancellationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReduceAsync_WhenSummarizerRuntimeCancels_PreservesExceptionAndDoesNotRunFallback(bool taskCanceled)
    {
        using var cancellation = new CancellationTokenSource();
        OperationCanceledException original = taskCanceled
            ? new TaskCanceledException("Caller canceled reduction.", null, cancellation.Token)
            : new OperationCanceledException(cancellation.Token);
        var runtime = new Mock<IPromptReducerRuntimeClient>();
        runtime.Setup(client => client.GenerateAsync(
                It.IsAny<string>(), It.IsAny<string>(), null, cancellation.Token))
            .Returns(() =>
            {
                cancellation.Cancel();
                return Task.FromException<LlamaGenerationResult>(original);
            });
        var fallback = CreateFallback();
        var pipeline = new PromptReductionPipeline([CreateSummarizer(runtime.Object), fallback.Object]);

        Func<Task> reduce = () => pipeline.ReduceAsync("long prompt", 123, cancellation.Token);

        var failure = await reduce.Should().ThrowAsync<OperationCanceledException>();
        failure.Which.Should().BeSameAs(original);
        failure.Which.CancellationToken.Should().Be(cancellation.Token);
        VerifyFallbackWasNotCalled(fallback);
    }

    [Fact]
    public async Task ReduceAsync_WhenSummarizerRuntimeTimesOut_PreservesExceptionAndDoesNotRunFallback()
    {
        var original = new LlamaRuntimeTimeoutException("Prompt reducer deadline expired.");
        var runtime = new Mock<IPromptReducerRuntimeClient>();
        runtime.Setup(client => client.GenerateAsync(
                It.IsAny<string>(), It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(original);
        var fallback = CreateFallback();
        var pipeline = new PromptReductionPipeline([CreateSummarizer(runtime.Object), fallback.Object]);

        Func<Task> reduce = () => pipeline.ReduceAsync("long prompt", 123, CancellationToken.None);

        var failure = await reduce.Should().ThrowAsync<LlamaRuntimeTimeoutException>();
        failure.Which.Should().BeSameAs(original);
        VerifyFallbackWasNotCalled(fallback);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReduceAsync_WhenTokenIsAlreadyCanceled_DoesNotDispatchReducers(bool emptyPipeline)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var fallback = CreateFallback();
        var pipeline = new PromptReductionPipeline(emptyPipeline ? [] : [fallback.Object]);

        Func<Task> reduce = () => pipeline.ReduceAsync("long prompt", 123, cancellation.Token);

        var failure = await reduce.Should().ThrowAsync<OperationCanceledException>();
        failure.Which.CancellationToken.Should().Be(cancellation.Token);
        VerifyFallbackWasNotCalled(fallback);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReduceAsync_WhenReducerReturnsAfterCancellation_StopsWithoutAcceptingResultOrRunningFallback(bool wasReduced)
    {
        using var cancellation = new CancellationTokenSource();
        var reducer = new Mock<IPromptReducer>(MockBehavior.Strict);
        reducer.SetupGet(value => value.Order).Returns(100);
        reducer.Setup(value => value.ReduceAsync("long prompt", 123, cancellation.Token))
            .Returns(() =>
            {
                cancellation.Cancel();
                return Task.FromResult(new PromptReductionResult("short prompt", wasReduced, PromptReductionStrategy.LlmSummarizer));
            });
        var fallback = CreateFallback();
        var pipeline = new PromptReductionPipeline([reducer.Object, fallback.Object]);

        Func<Task> reduce = () => pipeline.ReduceAsync("long prompt", 123, cancellation.Token);

        var failure = await reduce.Should().ThrowAsync<OperationCanceledException>();
        failure.Which.CancellationToken.Should().Be(cancellation.Token);
        VerifyFallbackWasNotCalled(fallback);
    }

    private static LlmPromptSummarizer CreateSummarizer(IPromptReducerRuntimeClient runtime) => new(
        runtime,
        Options.Create(new PromptReducerRuntimeOptions()),
        Options.Create(new PromptReductionOptions()));

    private static Mock<IPromptReducer> CreateFallback()
    {
        var fallback = new Mock<IPromptReducer>(MockBehavior.Strict);
        fallback.SetupGet(reducer => reducer.Order).Returns(200);
        return fallback;
    }

    private static void VerifyFallbackWasNotCalled(Mock<IPromptReducer> fallback) =>
        fallback.Verify(reducer => reducer.ReduceAsync(
            It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
}
