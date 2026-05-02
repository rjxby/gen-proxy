namespace GenProxy.Api.Integrations.Contracts;

public sealed class LlamaRuntimeCallException(string message, Exception? innerException = null)
    : Exception(message, innerException);
