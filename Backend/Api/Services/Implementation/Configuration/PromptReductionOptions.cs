namespace GenProxy.Api.Services.Implementation.Configuration;

public class PromptReductionOptions
{
    public const string SectionName = "PromptReduction";

    public string SummarizationPromptTemplate { get; set; } =
        """
        Rewrite the following user input so it fits within the generation runtime's allowed input budget for this request while preserving the essential user intent, constraints, and required output details.
        Return only the shortened prompt text.

        Maximum allowed input tokens for this request: {{max_tokens}}

        Original user input:
        {{prompt}}
        """;
}
