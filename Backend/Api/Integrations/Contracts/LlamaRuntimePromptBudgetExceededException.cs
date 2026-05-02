namespace GenProxy.Api.Integrations.Contracts;

public sealed class LlamaRuntimePromptBudgetExceededException(string message, Exception? innerException = null)
    : Exception(message, innerException);
