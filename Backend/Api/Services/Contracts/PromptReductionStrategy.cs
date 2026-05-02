namespace GenProxy.Api.Services.Contracts;

public enum PromptReductionStrategy
{
    None = 0,
    LlmSummarizer = 1,
    LeadingTruncation = 2
}

public static class PromptReductionStrategyNames
{
    public const string None = "none";
    public const string LlmSummarizer = "llm_summarizer";
    public const string LeadingTruncation = "leading_truncation";

    public static string GetWireName(PromptReductionStrategy value)
    {
        return value switch
        {
            PromptReductionStrategy.None => None,
            PromptReductionStrategy.LlmSummarizer => LlmSummarizer,
            PromptReductionStrategy.LeadingTruncation => LeadingTruncation,
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unsupported prompt reduction strategy.")
        };
    }
}
