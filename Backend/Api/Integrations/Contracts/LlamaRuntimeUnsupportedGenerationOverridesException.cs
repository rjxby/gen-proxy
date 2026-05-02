namespace GenProxy.Api.Integrations.Contracts;

public sealed class LlamaRuntimeUnsupportedGenerationOverridesException(string message, Exception? innerException = null)
    : Exception(message, innerException);
