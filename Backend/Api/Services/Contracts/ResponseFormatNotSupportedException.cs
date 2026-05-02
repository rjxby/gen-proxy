namespace GenProxy.Api.Services.Contracts;

public sealed class ResponseFormatNotSupportedException(string message) : Exception(message);
