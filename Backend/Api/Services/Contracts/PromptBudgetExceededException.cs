namespace GenProxy.Api.Services.Contracts;

public sealed class PromptBudgetExceededException(string message) : Exception(message);
