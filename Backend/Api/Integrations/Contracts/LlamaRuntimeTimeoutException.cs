namespace GenProxy.Api.Integrations.Contracts;

public sealed class LlamaRuntimeTimeoutException(string message, Exception? innerException = null)
    : Exception(message, innerException);
