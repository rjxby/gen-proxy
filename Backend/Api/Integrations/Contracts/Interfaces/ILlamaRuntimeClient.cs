using GenProxy.Api.Integrations.Contracts.Models;

namespace GenProxy.Api.Integrations.Contracts;

public interface ILlamaRuntimeClient
{
    Task<TokenEstimation> EstimateTokensAsync(string prompt, CancellationToken cancellationToken);

    Task<LlamaGenerationResult> GenerateAsync(string requestId, string prompt, CancellationToken cancellationToken);
}
