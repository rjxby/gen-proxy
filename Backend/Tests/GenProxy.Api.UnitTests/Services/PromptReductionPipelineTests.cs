using FluentAssertions;
using GenProxy.Api.Services.Contracts;
using GenProxy.Api.Services.Contracts.Models;
using GenProxy.Api.Services.Implementation.Services;
using Xunit;

namespace GenProxy.Api.UnitTests;

public class PromptReductionPipelineTests
{
    [Fact]
    public async Task ReduceAsync_StopsAfterFirstSuccessfulReducerInExplicitOrder()
    {
        var calls = new List<string>();
        var pipeline = new PromptReductionPipeline(
        [
            new TestPromptReducer("fallback", 200, (_, _, _) =>
            {
                calls.Add("fallback");
                return Task.FromResult(new PromptReductionResult("short", false, PromptReductionStrategy.None));
            }),
            new TestPromptReducer("summary", 100, (_, _, _) =>
            {
                calls.Add("summary");
                return Task.FromResult(new PromptReductionResult("short", true, PromptReductionStrategy.LlmSummarizer));
            }),
            new TestPromptReducer("already-fits", 150, (prompt, _, _) =>
            {
                calls.Add("already-fits");
                return Task.FromResult(new PromptReductionResult(prompt, false, PromptReductionStrategy.None));
            })
        ]);

        var result = await pipeline.ReduceAsync("prompt", 100, CancellationToken.None);

        calls.Should().Equal("summary");
        result.Prompt.Should().Be("short");
        result.WasReduced.Should().BeTrue();
        result.Strategy.Should().Be(PromptReductionStrategy.LlmSummarizer);
    }

    [Fact]
    public async Task ReduceAsync_WhenLowerOrderReducerDoesNothing_UsesNextReducer()
    {
        var calls = new List<string>();
        var pipeline = new PromptReductionPipeline(
        [
            new TestPromptReducer("fallback", 200, (_, _, _) =>
            {
                calls.Add("fallback");
                return Task.FromResult(new PromptReductionResult("short", true, PromptReductionStrategy.LeadingTruncation));
            }),
            new TestPromptReducer("summary", 100, (_, _, _) =>
            {
                calls.Add("summary");
                return Task.FromResult(new PromptReductionResult("prompt", false, PromptReductionStrategy.None));
            })
        ]);

        var result = await pipeline.ReduceAsync("prompt", 100, CancellationToken.None);

        calls.Should().Equal("summary", "fallback");
        result.Prompt.Should().Be("short");
        result.WasReduced.Should().BeTrue();
        result.Strategy.Should().Be(PromptReductionStrategy.LeadingTruncation);
    }

    [Fact]
    public async Task ReduceAsync_WhenFirstReducerReduces_DoesNotRunLaterReducers()
    {
        var calls = new List<string>();
        var pipeline = new PromptReductionPipeline(
        [
            new TestPromptReducer("fallback", 200, (_, _, _) =>
            {
                calls.Add("fallback");
                return Task.FromResult(new PromptReductionResult("fits", true, PromptReductionStrategy.LeadingTruncation));
            }),
            new TestPromptReducer("summary", 100, (_, _, _) =>
            {
                calls.Add("summary");
                return Task.FromResult(new PromptReductionResult("still too long", true, PromptReductionStrategy.LlmSummarizer));
            })
        ]);

        var result = await pipeline.ReduceAsync("prompt", 100, CancellationToken.None);

        calls.Should().Equal("summary");
        result.Prompt.Should().Be("still too long");
        result.WasReduced.Should().BeTrue();
        result.Strategy.Should().Be(PromptReductionStrategy.LlmSummarizer);
    }

    [Fact]
    public async Task ReduceAsync_WhenFirstReducerThrows_PropagatesException()
    {
        var pipeline = new PromptReductionPipeline(
        [
            new TestPromptReducer("summary", 100, (_, _, _) => throw new InvalidOperationException("reducer runtime unavailable"))
        ]);

        var act = async () => await pipeline.ReduceAsync("prompt", 100, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    private sealed class TestPromptReducer(
        string name,
        int order,
        Func<string, int, CancellationToken, Task<PromptReductionResult>> reduceAsync) : IPromptReducer
    {
        public int Order { get; } = order;

        public Task<PromptReductionResult> ReduceAsync(string prompt, int maxAllowedInputTokens, CancellationToken cancellationToken)
            => reduceAsync(prompt, maxAllowedInputTokens, cancellationToken);

        public override string ToString() => name;
    }
}
