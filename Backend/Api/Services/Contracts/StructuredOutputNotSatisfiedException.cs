namespace GenProxy.Api.Services.Contracts;

public sealed class StructuredOutputNotSatisfiedException(string message) : Exception(message);
