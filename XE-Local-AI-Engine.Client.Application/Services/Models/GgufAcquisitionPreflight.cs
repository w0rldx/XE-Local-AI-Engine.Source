namespace XE_Local_AI_Engine.Client.Services.Models;

using System.Security.Cryptography;
using System.Text;
using XE_Local_AI_Engine.Client.Services.Validation;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

public enum GgufAcquisitionOperationKind
{
    Download,
    Import
}

public enum ProviderMapDisposition
{
    Absent,
    CompatibleLlamaCpp,
    ConflictingProvider
}

public sealed record GgufAcquisitionIntent(
    GgufAcquisitionOperationKind OperationKind,
    string ModelBaseName,
    string Quantization,
    bool HasProjector = false);

public sealed record ResolvedGgufAcquisitionIdentity(
    string CanonicalModelName,
    string ModelReservationKey,
    string CanonicalQuantization,
    string FinalFileName,
    string RelativeGgufPath,
    string RelativeSidecarPath,
    string? ProjectorFileName,
    string? ProjectorRelativePath);

public sealed record GgufAcquisitionState(
    bool HasRegistryCollision,
    bool HasMemberCollision,
    ProviderMapDisposition ProviderMapDisposition,
    string? ConflictingProvider = null);

public interface IGgufAcquisitionStateProbe
{
    Task<GgufAcquisitionState> ProbeAsync(ResolvedGgufAcquisitionIdentity identity,
        InstalledModelMutationLease lease,
        CancellationToken cancellationToken);
}

public interface IGgufAcquisitionPreflight
{
    Task<PreparedGgufAcquisition> ResolveAndReserveAsync(GgufAcquisitionIntent intent,
        CancellationToken cancellationToken = default);
}

public sealed class GgufAcquisitionIdentityResolver(ModelNameValidator modelNameValidator)
{
    private readonly ModelNameValidator _modelNameValidator = modelNameValidator ?? throw new ArgumentNullException(nameof(modelNameValidator));

    public ResolvedGgufAcquisitionIdentity Resolve(GgufAcquisitionIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        var modelBaseName = intent.ModelBaseName.Trim().Normalize(NormalizationForm.FormC);
        if (modelBaseName.Length == 0
            || modelBaseName.Contains(':')
            || !_modelNameValidator.IsValid(modelBaseName))
        {
            throw new ArgumentException("The model base name is invalid or already contains a quantization suffix.", nameof(intent));
        }

        var quantization = NormalizeQuantization(intent.Quantization);
        var canonicalModelName = GgufModelName.Format(modelBaseName, quantization);
        var reservationKey = ModelCoordinationKeys.NormalizeModelName(canonicalModelName);
        var slug = CreateSlug(modelBaseName);
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(reservationKey));
        var identityHash = Convert.ToHexString(hashBytes.AsSpan(0, 12)).ToLowerInvariant();
        var fileName = $"{slug}-{quantization}-{identityHash}.gguf";
        var projectorFileName = intent.HasProjector ? $"{slug}-projector-{identityHash}.gguf" : null;
        return new ResolvedGgufAcquisitionIdentity(
            canonicalModelName,
            reservationKey,
            quantization,
            fileName,
            fileName,
            $"{fileName}.xe-model.json",
            projectorFileName,
            projectorFileName);
    }

    private static string NormalizeQuantization(string quantization)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(quantization);
        var normalized = quantization.Trim().ToUpperInvariant().Replace("UD_", "UD-", StringComparison.Ordinal);
        var parsed = GgufQuantParser.TryParse($"model-{normalized}.gguf");
        if (parsed is null || !string.Equals(parsed, normalized, StringComparison.Ordinal))
        {
            throw new ArgumentException("The quantization is not a repository-owned canonical GGUF quantization.", nameof(quantization));
        }

        return parsed;
    }

    private static string CreateSlug(string modelBaseName)
    {
        var builder = new StringBuilder(modelBaseName.Length);
        var previousDash = false;
        foreach (var rune in modelBaseName.EnumerateRunes())
        {
            var value = rune.Value;
            var allowed = value is >= 'a' and <= 'z'
                          || value is >= 'A' and <= 'Z'
                          || value is >= '0' and <= '9'
                          || value is '_' or '-' or '.';
            if (allowed)
            {
                var character = (char)value;
                builder.Append(char.ToLowerInvariant(character));
                previousDash = character == '-';
            }
            else if (!previousDash)
            {
                builder.Append('-');
                previousDash = true;
            }
        }

        var slug = builder.ToString().Trim('.', '-', '_', ' ');
        if (slug.Length == 0)
        {
            slug = "model";
        }

        return slug.Length <= 72 ? slug : slug[..72].TrimEnd('.', '-', '_', ' ');
    }
}

public sealed class GgufAcquisitionPreflight(
    GgufAcquisitionIdentityResolver identityResolver,
    IInstalledModelSnapshotCoordinator snapshotCoordinator,
    IGgufAcquisitionStateProbe stateProbe) : IGgufAcquisitionPreflight
{
    private readonly GgufAcquisitionIdentityResolver _identityResolver = identityResolver ?? throw new ArgumentNullException(nameof(identityResolver));
    private readonly IInstalledModelSnapshotCoordinator _snapshotCoordinator = snapshotCoordinator ?? throw new ArgumentNullException(nameof(snapshotCoordinator));
    private readonly IGgufAcquisitionStateProbe _stateProbe = stateProbe ?? throw new ArgumentNullException(nameof(stateProbe));

    public async Task<PreparedGgufAcquisition> ResolveAndReserveAsync(GgufAcquisitionIntent intent,
        CancellationToken cancellationToken = default)
    {
        var identity = _identityResolver.Resolve(intent);
        var members = new List<IntendedInstalledModelMember>
        {
            new(identity.RelativeGgufPath, "weight"),
            new(identity.RelativeSidecarPath, "sidecar")
        };
        if (identity.ProjectorRelativePath is not null)
        {
            members.Add(new IntendedInstalledModelMember(identity.ProjectorRelativePath, "projector"));
        }

        var lease = await _snapshotCoordinator.AcquireMutationAsync(
            new InstalledModelMutationRequest(identity.CanonicalModelName, InstalledModelMutationKind.Acquire, members),
            cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await _stateProbe.ProbeAsync(identity, lease, cancellationToken).ConfigureAwait(false);
            if (state.HasRegistryCollision || state.HasMemberCollision || state.ProviderMapDisposition == ProviderMapDisposition.ConflictingProvider)
            {
                throw new InvalidOperationException("ModelConflict");
            }

            return new PreparedGgufAcquisition(identity, state.ProviderMapDisposition, lease);
        }
        catch
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

public sealed class PreparedGgufAcquisition : IAsyncDisposable
{
    private InstalledModelMutationLease? _lease;

    internal PreparedGgufAcquisition(ResolvedGgufAcquisitionIdentity identity,
        ProviderMapDisposition providerMapDisposition,
        InstalledModelMutationLease lease)
    {
        Identity = identity;
        ProviderMapDisposition = providerMapDisposition;
        _lease = lease;
    }

    public ResolvedGgufAcquisitionIdentity Identity { get; }
    public ProviderMapDisposition ProviderMapDisposition { get; }
    public InstalledModelMutationLease Lease => _lease ?? throw new ObjectDisposedException(nameof(PreparedGgufAcquisition));

    public InstalledModelMutationLease TransferLease()
    {
        return Interlocked.Exchange(ref _lease, null) ?? throw new InvalidOperationException("The acquisition reservation lease was already transferred.");
    }

    public async ValueTask DisposeAsync()
    {
        var lease = Interlocked.Exchange(ref _lease, null);
        if (lease is not null)
        {
            await lease.DisposeAsync().ConfigureAwait(false);
        }
    }
}
