using GenProxy.Api.Services.Contracts.Models;

namespace GenProxy.Api.Services.Contracts;

public interface IResponseGenerationService
{
    Task<GeneratedResponse> GenerateAsync(ResponseCreateCommand command, CancellationToken cancellationToken);
}
