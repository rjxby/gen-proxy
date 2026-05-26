namespace GenProxy.Api.Integrations.Contracts;

public sealed class LlamaRuntimeStructuredOutputNotSatisfiedException(string message, Exception? innerException = null)
    : Exception(message, innerException);
