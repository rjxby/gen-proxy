namespace GenProxy.Api.Integrations.Contracts;

public sealed class UpstreamRuntimeException(string message, Exception? innerException = null)
    : Exception(message, innerException);
