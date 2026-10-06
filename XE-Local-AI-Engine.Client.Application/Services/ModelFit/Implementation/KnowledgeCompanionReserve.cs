namespace XE_Local_AI_Engine.Client.Services.ModelFit.Implementation;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Default <see cref="IKnowledgeCompanionReserve" />: sums the capacity gate's own footprint for the configured
///     reranker and the llama.cpp embedder, skipping a resident one only when the budget is measured free VRAM.
/// </summary>
/// <remarks>
///     The embedder counts only on the llama.cpp provider and under a confidently resolved installed name, the name the
///     knowledge lanes embed with. Any failure to resolve a companion counts it as zero: recommendations must not fail on it.
/// </remarks>
public sealed class KnowledgeCompanionReserve : IKnowledgeCompanionReserve
{
    private readonly IModelFootprintProvider _footprintProvider;
    private readonly ILlamaServerProcessSupervisor _supervisor;
    private readonly ILocalModelProviderResolver _providerResolver;
    private readonly IEmbeddingModelResolver _embeddingModelResolver;
    private readonly KnowledgeBaseOptions _options;
    private readonly ILogger<KnowledgeCompanionReserve> _logger;

    public KnowledgeCompanionReserve(IModelFootprintProvider footprintProvider,
        ILlamaServerProcessSupervisor supervisor,
        ILocalModelProviderResolver providerResolver,
        IEmbeddingModelResolver embeddingModelResolver,
        IOptions<KnowledgeBaseOptions> options,
        ILogger<KnowledgeCompanionReserve> logger)
    {
        _footprintProvider = footprintProvider ?? throw new ArgumentNullException(nameof(footprintProvider));
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        _providerResolver = providerResolver ?? throw new ArgumentNullException(nameof(providerResolver));
        _embeddingModelResolver = embeddingModelResolver ?? throw new ArgumentNullException(nameof(embeddingModelResolver));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<long> ResolveGpuBytesAsync(HardwareProfile profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var companions = new List<Companion>(capacity: 2);
        if (!string.IsNullOrWhiteSpace(_options.RerankerModelName))
        {
            companions.Add(new Companion(_options.RerankerModelName, ModelRole.Reranker));
        }

        if (await TryResolveLlamaCppEmbedderAsync(ct) is { } embedder)
        {
            companions.Add(new Companion(embedder, ModelRole.Embedding));
        }

        // Only a measured free figure (the same test the fit budget uses) already nets out a resident companion; total VRAM does not.
        if (profile.AvailableVramBytes is > 0)
        {
            var resident = _supervisor.ListRunningProcesses();
            companions.RemoveAll(companion => resident.Any(process => process.Role == companion.Role
                                                                      && string.Equals(process.ModelName, companion.ModelName, StringComparison.OrdinalIgnoreCase)));
        }

        if (companions.Count == 0)
        {
            return 0;
        }

        var total = 0L;
        try
        {
            foreach (var (modelName, role) in companions)
            {
                var footprint = await _footprintProvider.ResolveFootprintAsync(modelName, role, profile, ct);
                total += footprint.IsKnown ? footprint.Resources.GpuBytes : 0;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Knowledge companion footprint could not be resolved; model recommendations reserve {Bytes} bytes for companions.", total);
        }

        return total;
    }

    private async Task<string?> TryResolveLlamaCppEmbedderAsync(CancellationToken ct)
    {
        if (!string.Equals(_options.EmbeddingProviderName, LlamaServerProviderConstants.ProviderName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            var provider = _providerResolver.ResolveProvider(_options.EmbeddingProviderName);
            var resolution = await _embeddingModelResolver.ResolveAsync(provider, ct);
            return resolution.IsConfident ? resolution.Name : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Knowledge embedder could not be resolved; model recommendations reserve nothing for it.");
            return null;
        }
    }

    /// <summary>
    ///     A copy of <paramref name="profile" /> whose VRAM figures are reduced by <paramref name="reserveBytes" />, never
    ///     below one byte so GPU mode cannot flip to CPU mode; the profile itself when there is nothing to reserve.
    /// </summary>
    public static HardwareProfile ApplyTo(HardwareProfile profile, long reserveBytes)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (reserveBytes <= 0)
        {
            return profile;
        }

        return profile with
        {
            VramBytes = Reduce(profile.VramBytes, reserveBytes),
            AvailableVramBytes = Reduce(profile.AvailableVramBytes, reserveBytes)
        };
    }

    /// <summary>Reduces a positive VRAM figure by the reserve, clamped to one byte; an absent or non-positive figure is left as is.</summary>
    private static long? Reduce(long? bytes, long reserveBytes) =>
        bytes is > 0 ? Math.Max(1, bytes.Value - reserveBytes) : bytes;

    private readonly record struct Companion(string ModelName, ModelRole Role);
}
