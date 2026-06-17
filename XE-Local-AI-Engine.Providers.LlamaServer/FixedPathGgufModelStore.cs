namespace XE_Local_AI_Engine.Providers.LlamaServer;

using XE_Local_AI_Engine.HostAgent.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     Temporary fake <see cref="IGgufModelStore" /> for Lane A until Lane B lands the real HF GGUF store. Resolves
///     every requested model name to a single fixed path and reports a fixed installed-model list. This unblocks the
///     supervisor/provider tests; it MUST be replaced by Lane B's store in production DI.
/// </summary>
public sealed class FixedPathGgufModelStore : IGgufModelStore
{
    private readonly string _fixedModelFilePath;
    private readonly IReadOnlyList<string> _installedModelNames;

    /// <summary>Creates a fake store that maps any model to <paramref name="fixedModelFilePath" />.</summary>
    /// <param name="fixedModelFilePath">Path returned for every <see cref="ResolveModelFilePathAsync" /> call.</param>
    /// <param name="installedModelNames">Model names reported by <see cref="ListInstalledModelsAsync" />.</param>
    public FixedPathGgufModelStore(string fixedModelFilePath, IReadOnlyList<string>? installedModelNames = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fixedModelFilePath);
        _fixedModelFilePath = fixedModelFilePath;
        _installedModelNames = installedModelNames ?? [];
    }

    /// <inheritdoc />
    public Task<string?> ResolveModelFilePathAsync(string modelName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        return Task.FromResult<string?>(_fixedModelFilePath);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<LocalModelDescriptor>> ListInstalledModelsAsync(CancellationToken ct)
    {
        IReadOnlyList<LocalModelDescriptor> descriptors = _installedModelNames
            .Select(name => new LocalModelDescriptor
            {
                ModelName = name,
                ProviderName = LlamaServerProviderConstants.ProviderName,
                IsAvailable = true,
                SizeBytes = null,
                ModifiedAt = null,
                MaxContextTokens = null
            })
            .ToList();

        return Task.FromResult(descriptors);
    }
}
