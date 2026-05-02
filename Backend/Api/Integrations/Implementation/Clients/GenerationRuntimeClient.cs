using GenProxy.Api.Integrations.Contracts;
using GenProxy.Api.Integrations.Contracts.Models;

namespace GenProxy.Api.Integrations.Implementation.Clients;

public sealed class GenerationRuntimeClient(ILlamaRuntimeClient inner) : IGenerationRuntimeClient
{
    private readonly ILlamaRuntimeClient _inner = inner;

    public Task<TokenEstimation> EstimateTokensAsync(string prompt, CancellationToken cancellationToken)
        => _inner.EstimateTokensAsync(prompt, cancellationToken);

    public Task<LlamaCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken)
        => _inner.GetCapabilitiesAsync(cancellationToken);

    public Task<LlamaGenerationResult> GenerateAsync(
        string requestId,
        string prompt,
        LlamaGenerationOptions? options,
        CancellationToken cancellationToken)
        => _inner.GenerateAsync(requestId, prompt, options, cancellationToken);
}
