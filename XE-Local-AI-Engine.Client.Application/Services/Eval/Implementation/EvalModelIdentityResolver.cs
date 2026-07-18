namespace XE_Local_AI_Engine.Client.Services.Eval.Implementation;

using System.Globalization;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>
///     Default <see cref="IEvalModelIdentityResolver" />. Resolves the strongest weight identity available for the
///     configured eval model across the two local runtimes, preferring the default llama.cpp runtime:
///     <list type="number">
///         <item>
///             <b>llama.cpp GGUF</b> (the default runtime) via the on-disk GGUF registry: the verified content hash
///             (the LFS OID) when exposed — a true weight identity — else revision + on-disk size + download-time, all of
///             which change when the model is re-downloaded under the same name.
///         </item>
///         <item>
///             <b>Ollama</b> (the gated secondary runtime) via the digest-keyed model-classification cache: the model's
///             Ollama content digest, present when the model was probed via <c>/api/show</c>.
///         </item>
///         <item>
///             Neither resolvable → <see cref="EvalModelIdentity.Unverified" />: an explicit sentinel, logged as a
///             Warning, so the fingerprint records the run as identity-unverifiable rather than silently trusting the name.
///         </item>
///     </list>
///     The identity for the SAME weights is stable across reads (download-time / hash / digest are persisted, never
///     "now"), so a fingerprint recorded at eval time still matches at promote time when no swap happened. Never throws:
///     any lookup failure falls through to the next source and ultimately to the unverified sentinel.
/// </summary>
internal sealed class EvalModelIdentityResolver(
    IGgufModelRegistry ggufRegistry,
    IModelClassificationStore classificationStore,
    ILogger<EvalModelIdentityResolver> logger) : IEvalModelIdentityResolver
{
    private readonly IModelClassificationStore _classificationStore = classificationStore ?? throw new ArgumentNullException(nameof(classificationStore));
    private readonly IGgufModelRegistry _ggufRegistry = ggufRegistry ?? throw new ArgumentNullException(nameof(ggufRegistry));
    private readonly ILogger<EvalModelIdentityResolver> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task<EvalModelIdentity> ResolveAsync(string modelName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return EvalModelIdentity.Unverified;
        }

        // (1) llama.cpp GGUF — the default runtime. The registry carries the strongest weight identity: the verified
        // content hash when the LFS OID was exposed, else revision + size + download-time (a re-download always changes
        // DownloadedAtUtc, so a same-name swap invalidates even without a hash).
        try
        {
            var entry = await _ggufRegistry.FindAsync(modelName, cancellationToken).ConfigureAwait(false);
            if (entry is not null)
            {
                var token = !string.IsNullOrEmpty(entry.Sha256)
                    ? $"gguf-sha256:{entry.Sha256}"
                    : string.Create(CultureInfo.InvariantCulture,
                        $"gguf-rev:{entry.SourceRevision}:size:{entry.SizeBytes}:dl:{entry.DownloadedAtUtc.ToUnixTimeMilliseconds()}");
                return new EvalModelIdentity(token, IsVerified: true);
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
            var classification = await _classificationStore.GetByNameAsync(modelName, cancellationToken).ConfigureAwait(false);
            if (classification is not null && !string.IsNullOrWhiteSpace(classification.Digest))
            {
                return new EvalModelIdentity($"ollama-digest:{classification.Digest}", IsVerified: true);
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

        // (3) No identity source resolved — record the explicit unverified sentinel rather than silently trusting the
        // name. A later run that CAN resolve an identity produces a different (verified) token, so the fingerprint
        // changes and forces a re-eval the moment the model becomes identifiable.
        _logger.LogWarning(
            "Could not resolve a weight identity for eval model {ModelName}; the eval fingerprint records it as identity-unverified so a same-name weight swap cannot silently keep a recorded pass trusted against different weights.",
            modelName);
        return EvalModelIdentity.Unverified;
    }
}
