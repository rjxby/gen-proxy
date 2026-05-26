namespace GenProxy.Api.Integrations.Contracts;

public sealed class LlamaRuntimeInvalidArgumentException(string message, Exception? innerException = null)
    : Exception(message, innerException);
