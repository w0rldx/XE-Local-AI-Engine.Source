namespace XE_Local_AI_Engine.Client.Services.Eval.Implementation;

using System.Globalization;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>
///     Default <see cref="IEvalModelIdentityResolver" />, resolving a model's weight identity from the GGUF registry.
/// </summary>
/// <remarks>
///     A GGUF resolves from the on-disk registry: the verified content hash when the LFS OID is exposed, else
///     revision, size and download-time, all of which move on a same-name re-download. Anything else yields
///     <see cref="EvalModelIdentity.Unverified" />, an explicit sentinel logged as a Warning. The identity for the SAME
///     weights is stable across reads, since the source is persisted rather than "now", and a lookup failure never throws.
/// </remarks>
internal sealed class EvalModelIdentityResolver : IEvalModelIdentityResolver
{
    private readonly IGgufModelRegistry _ggufRegistry;
    private readonly ILogger<EvalModelIdentityResolver> _logger;

    public EvalModelIdentityResolver(IGgufModelRegistry ggufRegistry,
        ILogger<EvalModelIdentityResolver> logger)
    {
        ArgumentNullException.ThrowIfNull(ggufRegistry);
        ArgumentNullException.ThrowIfNull(logger);
        _ggufRegistry = ggufRegistry;
        _logger = logger;
    }

    public async Task<EvalModelIdentity> ResolveAsync(string modelName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return EvalModelIdentity.Unverified;
        }

        // The registry carries the identity: the content hash when the LFS OID was exposed, else revision, size and a
        // download-time a re-download always moves.
        try
        {
            var entry = await _ggufRegistry.FindAsync(modelName, cancellationToken);
            if (entry is not null)
            {
                var token = !string.IsNullOrEmpty(entry.Sha256)
                    ? $"gguf-sha256:{entry.Sha256}"
                    : string.Create(CultureInfo.InvariantCulture,
                        $"gguf-rev:{entry.SourceRevision}:size:{entry.SizeBytes}:dl:{entry.DownloadedAtUtc.ToUnixTimeMilliseconds()}");
                return new EvalModelIdentity
                {
                    Token = token,
                    IsVerified = true
                };
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "GGUF registry weight-identity lookup failed for eval model {ModelName}.", modelName);
        }

        // No identity resolved, so record the explicit unverified sentinel rather than trust the name. A
        // later run that CAN resolve one produces a verified token, forcing a re-eval the moment it becomes possible.
        _logger.LogWarning(
            "Could not resolve a weight identity for eval model {ModelName}; the eval fingerprint records it as identity-unverified so a same-name weight swap cannot silently keep a recorded pass trusted against different weights.",
            modelName);
        return EvalModelIdentity.Unverified;
    }
}
