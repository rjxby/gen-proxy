namespace GenProxy.Api.Integrations.Contracts;

public sealed class UpstreamPromptBudgetExceededException(string message, Exception? innerException = null)
    : Exception(message, innerException);
