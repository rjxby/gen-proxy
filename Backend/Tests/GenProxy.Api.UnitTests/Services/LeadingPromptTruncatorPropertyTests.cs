using System.Text;
using FluentAssertions;
using GenProxy.Api.Integrations.Contracts;
using GenProxy.Api.Integrations.Contracts.Models;
using GenProxy.Api.Services.Implementation.Services;
using Moq;
using Xunit;

namespace GenProxy.Api.UnitTests.Services;

public class LeadingPromptTruncatorPropertyTests
{
    [Theory]
    [InlineData(17)]
    [InlineData(103)]
    [InlineData(2026)]
    [InlineData(65537)]
    public async Task GeneratedUnicodePrompts_OnlySendValidSuffixesAndTerminateWithinBudget(int seed)
    {
        var random = new Random(seed);
        string[] scalars = ["a", "界", "😀", "𐐷", "é", "\u0301", "\n", "\t"];
        var strictUtf8 = new UTF8Encoding(false, true);
        for (var sample = 0; sample < 64; sample++)
        {
            var prompt = string.Concat(Enumerable.Range(0, random.Next(1, 80)).Select(_ => scalars[random.Next(scalars.Length)])) + "z";
            var budget = random.Next(0, 40);
            var observed = new List<string>();
            var runtime = new Mock<IGenerationRuntimeClient>(MockBehavior.Strict);
            runtime.Setup(client => client.EstimateTokensAsync(It.IsAny<string>(), CancellationToken.None))
                .ReturnsAsync((string text, CancellationToken _) =>
                {
                    observed.Add(text);
                    strictUtf8.GetBytes(text);
                    var count = text.EnumerateRunes().Count();
                    return new TokenEstimation(count, 128, 16, budget, count <= budget);
                });

            var result = await new LeadingPromptTruncator(runtime.Object).ReduceAsync(prompt, budget, CancellationToken.None);

            result.Prompt.EnumerateRunes().Count().Should().BeLessThanOrEqualTo(budget, $"seed={seed}, sample={sample}");
            prompt.Should().EndWith(result.Prompt);
            observed.Count.Should().BeLessThanOrEqualTo(prompt.EnumerateRunes().Count());
            for (var index = 1; index < observed.Count; index++)
            {
                observed[index].Length.Should().BeLessThan(observed[index - 1].Length);
            }
            runtime.Verify(client => client.EstimateTokensAsync(It.IsAny<string>(), CancellationToken.None), Times.Exactly(observed.Count));
            runtime.VerifyNoOtherCalls();
        }
    }
}
