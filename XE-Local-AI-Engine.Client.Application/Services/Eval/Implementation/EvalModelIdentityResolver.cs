namespace XE_Local_AI_Engine.Client.Services.Eval.Implementation;

using System.Globalization;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>
///     Default <see cref="IEvalModelIdentityResolver" />, resolving the strongest weight identity available across
///     the two local runtimes and preferring the default llama.cpp one.
/// </summary>
/// <remarks>
///     A GGUF resolves from the on-disk registry: the verified content hash when the LFS OID is exposed, else
///     revision, size and download-time, all of which move on a same-name re-download. An Ollama model resolves from
///     the digest-keyed classification cache. Neither yields <see cref="EvalModelIdentity.Unverified" />, an explicit
///     sentinel logged as a Warning. The identity for the SAME weights is stable across reads, since every source is
///     persisted rather than "now", and no lookup failure throws: it falls through to the next source.
/// </remarks>
internal sealed class EvalModelIdentityResolver : IEvalModelIdentityResolver
{
    private readonly IModelClassificationStore _classificationStore;
    private readonly IGgufModelRegistry _ggufRegistry;
    private readonly ILogger<EvalModelIdentityResolver> _logger;

    public EvalModelIdentityResolver(
        IGgufModelRegistry ggufRegistry,
        IModelClassificationStore classificationStore,
        ILogger<EvalModelIdentityResolver> logger)
    {
        ArgumentNullException.ThrowIfNull(classificationStore);
        ArgumentNullException.ThrowIfNull(ggufRegistry);
        ArgumentNullException.ThrowIfNull(logger);
        _classificationStore = classificationStore;
        _ggufRegistry = ggufRegistry;
        _logger = logger;
    }

    public async Task<EvalModelIdentity> ResolveAsync(string modelName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return EvalModelIdentity.Unverified;
        }

        // (1) llama.cpp GGUF, the default runtime, whose registry carries the strongest identity: the content hash
        // when the LFS OID was exposed, else revision, size and a download-time a re-download always moves.
        try
        {
            var entry = await _ggufRegistry.FindAsync(modelName, cancellationToken);
            if (entry is not null)
            {
                var token = !string.IsNullOrEmpty(entry.Sha256)
                    ? $"gguf-sha256:{entry.Sha256}"
                    : string.Create(CultureInfo.InvariantCulture,
                        $"gguf-rev:{entry.SourceRevision}:size:{entry.SizeBytes}:dl:{entry.DownloadedAtUtc.ToUnixTimeMilliseconds()}");
                return new EvalModelIdentity { Token = token, IsVerified = true };
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "GGUF registry weight-identity lookup failed for eval model {ModelName}; falling back to the Ollama classification digest.", modelName);
        }

        // (2) Ollama — the gated secondary runtime. The classification store caches the model's content digest (its
        // Ollama manifest sha256) keyed by name, present only when the model was probed via /api/show.
        try
        {
            var classification = await _classificationStore.GetByNameAsync(modelName, cancellationToken);
            if (classification is not null && !string.IsNullOrWhiteSpace(classification.Digest))
            {
                return new EvalModelIdentity { Token = $"ollama-digest:{classification.Digest}", IsVerified = true };
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Classification-store weight-identity lookup failed for eval model {ModelName}.", modelName);
        }

        // (3) No identity source resolved, so record the explicit unverified sentinel rather than trust the name. A
        // later run that CAN resolve one produces a verified token, forcing a re-eval the moment it becomes possible.
        _logger.LogWarning(
            "Could not resolve a weight identity for eval model {ModelName}; the eval fingerprint records it as identity-unverified so a same-name weight swap cannot silently keep a recorded pass trusted against different weights.",
            modelName);
        return EvalModelIdentity.Unverified;
    }
}
