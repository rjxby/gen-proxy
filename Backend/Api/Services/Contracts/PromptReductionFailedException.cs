namespace GenProxy.Api.Services.Contracts;

public sealed class PromptReductionFailedException(string message, Exception? innerException = null)
    : Exception(message, innerException);
