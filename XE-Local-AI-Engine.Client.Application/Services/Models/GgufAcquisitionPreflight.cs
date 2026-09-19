namespace XE_Local_AI_Engine.Client.Services.Models;

using System.Diagnostics.CodeAnalysis;
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

public sealed record GgufAcquisitionIntent
{
    public required GgufAcquisitionOperationKind OperationKind { get; init; }

    public required string ModelBaseName { get; init; }

    public required string Quantization { get; init; }

    public GgufProjectorAcquisitionMetadata? Projector { get; init; }

    public GgufDownloadAcquisitionMetadata? Download { get; init; }
}

public sealed record GgufDownloadAcquisitionMetadata
{
    public required string RepoId { get; init; }

    public required string ResolvedRevision { get; init; }

    public required string SourceDisplayName { get; init; }

    public required long DeclaredSizeBytes { get; init; }

    public required string? DeclaredSha256 { get; init; }

    public required GgufRole Role { get; init; }
}

public sealed class GgufProjectorAcquisitionMetadata
{
    public required string SourceDisplayName { get; init; }

    public required string DeclaredSha256 { get; init; }

    public required long DeclaredSizeBytes { get; init; }
}

public enum GgufAcquisitionDisposition
{
    VerifiedInstalled,
    VerifiedLegacyInstalled,
    ActiveCompatible,
    Conflict,
    Available
}

public sealed class ResolvedGgufAcquisitionIdentity
{
    public required string CanonicalModelName { get; init; }

    public required string ModelReservationKey { get; init; }

    public required string CanonicalQuantization { get; init; }

    public required string FinalFileName { get; init; }

    public required string RelativeGgufPath { get; init; }

    public required string RelativeSidecarPath { get; init; }

    public required string? ProjectorFileName { get; init; }

    public required string? ProjectorRelativePath { get; init; }
}

public sealed class GgufAcquisitionState
{
    public required GgufAcquisitionDisposition Disposition { get; init; }

    public required ProviderMapDisposition ProviderMapDisposition { get; init; }

    public string? ConflictingProvider { get; init; }

    public Guid? ActiveOperationId { get; init; }
}

public interface IGgufAcquisitionPreflight
{
    Task<PreparedGgufAcquisition> ResolveAndReserveAsync(GgufAcquisitionIntent intent,
        CancellationToken cancellationToken = default);
}

public sealed class GgufAcquisitionIdentityResolver
{
    private readonly ModelNameValidator _modelNameValidator;

    public GgufAcquisitionIdentityResolver(ModelNameValidator modelNameValidator)
    {
        ArgumentNullException.ThrowIfNull(modelNameValidator);
        _modelNameValidator = modelNameValidator;
    }

    public static IReadOnlyList<string> CanonicalQuantizationChoices => QuantLadder.CanonicalQuantizations;

    public ResolvedGgufAcquisitionIdentity Resolve(GgufAcquisitionIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        var modelBaseName = intent.ModelBaseName.Trim().Normalize(NormalizationForm.FormC);
        if (modelBaseName.Length == 0
            || modelBaseName.Contains(':', StringComparison.Ordinal)
            || !_modelNameValidator.IsValid(modelBaseName))
        {
            throw new ArgumentException("The model base name is invalid or already contains a quantization suffix.", nameof(intent));
        }

        var quantization = NormalizeQuantization(intent.Quantization);
        var canonicalModelName = GgufModelName.Format(modelBaseName, quantization);
        var reservationKey = ModelCoordinationKeys.NormalizeModelName(canonicalModelName);
        var slug = CreateSlug(modelBaseName);
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(reservationKey));
        var identityHash = Convert.ToHexStringLower(hashBytes.AsSpan(0, 12));
        var fileName = $"{slug}-{quantization}-{identityHash}.gguf";
        ValidateDownloadMetadata(intent);
        ValidateProjectorMetadata(intent);
        var projectorFileName = intent.Projector is not null ? $"{slug}-projector-{identityHash}.gguf" : null;
        return new ResolvedGgufAcquisitionIdentity
        {
            CanonicalModelName = canonicalModelName,
            ModelReservationKey = reservationKey,
            CanonicalQuantization = quantization,
            FinalFileName = fileName,
            RelativeGgufPath = fileName,
            RelativeSidecarPath = $"{fileName}.xe-model.json",
            ProjectorFileName = projectorFileName,
            ProjectorRelativePath = projectorFileName
        };
    }

    private static void ValidateDownloadMetadata(GgufAcquisitionIntent intent)
    {
        if (intent.OperationKind == GgufAcquisitionOperationKind.Import && intent.Download is not null)
        {
            throw new ArgumentException("Local imports cannot carry resolved download metadata.", nameof(intent));
        }

        if (intent.Download is not { } download)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(download.RepoId)
            || string.IsNullOrWhiteSpace(download.ResolvedRevision)
            || !string.Equals(download.SourceDisplayName, Path.GetFileName(download.SourceDisplayName), StringComparison.Ordinal)
            || download.DeclaredSizeBytes <= 0
            || download.DeclaredSha256 is not null && !IsCanonicalSha256(download.DeclaredSha256))
        {
            throw new ArgumentException("The resolved download metadata is invalid.", nameof(intent));
        }
    }

    private static void ValidateProjectorMetadata(GgufAcquisitionIntent intent)
    {
        if (intent.OperationKind == GgufAcquisitionOperationKind.Import && intent.Projector is not null)
        {
            throw new ArgumentException("Local imports cannot include a projector.", nameof(intent));
        }

        if (intent.Projector is not { } projector)
        {
            return;
        }

        if (!string.Equals(projector.SourceDisplayName, Path.GetFileName(projector.SourceDisplayName), StringComparison.Ordinal)
            || projector.DeclaredSizeBytes <= 0
            || !IsCanonicalSha256(projector.DeclaredSha256))
        {
            throw new ArgumentException("The projector metadata is invalid.", nameof(intent));
        }
    }

    private static bool IsCanonicalSha256(string value) =>
        value.Length == 64 && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

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

    [SuppressMessage("Major Code Smell",
        "S3267:Loops should be simplified with LINQ expressions",
        Justification = "Slug construction is a stateful single-pass normalization that cannot be expressed clearly as Select().")]
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

/// <summary>
///     Signals that a GGUF acquisition (download or import) cannot proceed because the canonical model name, its
///     destination files, or its <c>model_provider_map</c> row are already claimed by something else. Typed so the HTTP
///     boundary and the import coordinator map it to 409 without matching on an exception message. The message is
///     sanitized and safe to surface — it never carries a path, URL, or token.
/// </summary>
public sealed class GgufAcquisitionConflictException : Exception
{
    /// <summary>Creates the conflict with the sanitized, operator-facing message.</summary>
    public GgufAcquisitionConflictException()
        : base("The model name or destination is already in use.")
    {
    }
}

public sealed class GgufAcquisitionPreflight : IGgufAcquisitionPreflight
{
    private readonly GgufAcquisitionIdentityResolver _identityResolver;
    private readonly IInstalledModelSnapshotCoordinator _snapshotCoordinator;
    private readonly GgufAcquisitionStateProbe _stateProbe;

    public GgufAcquisitionPreflight(
        GgufAcquisitionIdentityResolver identityResolver,
        IInstalledModelSnapshotCoordinator snapshotCoordinator,
        GgufAcquisitionStateProbe stateProbe)
    {
        ArgumentNullException.ThrowIfNull(identityResolver);
        ArgumentNullException.ThrowIfNull(snapshotCoordinator);
        ArgumentNullException.ThrowIfNull(stateProbe);
        _identityResolver = identityResolver;
        _snapshotCoordinator = snapshotCoordinator;
        _stateProbe = stateProbe;
    }

    public async Task<PreparedGgufAcquisition> ResolveAndReserveAsync(GgufAcquisitionIntent intent,
        CancellationToken cancellationToken = default)
    {
        var identity = _identityResolver.Resolve(intent);
        var members = new List<IntendedInstalledModelMember>
        {
            new() { RelativePath = identity.RelativeGgufPath, Role = InstalledModelPhysicalMemberRole.Weight },
            new() { RelativePath = identity.RelativeSidecarPath, Role = InstalledModelPhysicalMemberRole.Sidecar }
        };
        if (identity.ProjectorRelativePath is not null)
        {
            members.Add(new IntendedInstalledModelMember { RelativePath = identity.ProjectorRelativePath, Role = InstalledModelPhysicalMemberRole.Projector });
        }

        var lease = await _snapshotCoordinator.AcquireMutationAsync(new InstalledModelMutationRequest { ModelName = identity.CanonicalModelName, Kind = InstalledModelMutationKind.Acquire, IntendedMembers = members },
            cancellationToken);
        try
        {
            var state = await _stateProbe.ProbeAsync(intent, identity, lease, cancellationToken);
            if (state.Disposition == GgufAcquisitionDisposition.Conflict
                || state.ProviderMapDisposition == ProviderMapDisposition.ConflictingProvider
                || (intent.OperationKind == GgufAcquisitionOperationKind.Import && state.Disposition != GgufAcquisitionDisposition.Available))
            {
                throw new GgufAcquisitionConflictException();
            }

            if (state.Disposition == GgufAcquisitionDisposition.ActiveCompatible)
            {
                await lease.DisposeAsync();
                return new PreparedGgufAcquisition(identity, state.Disposition, state.ProviderMapDisposition, lease: null, state.ActiveOperationId);
            }

            return new PreparedGgufAcquisition(identity, state.Disposition, state.ProviderMapDisposition, lease, state.ActiveOperationId);
        }
        catch
        {
            await lease.DisposeAsync();
            throw;
        }
    }
}

public sealed class PreparedGgufAcquisition : IAsyncDisposable
{
    private InstalledModelMutationLease? _lease;

    internal PreparedGgufAcquisition(ResolvedGgufAcquisitionIdentity identity,
        GgufAcquisitionDisposition disposition,
        ProviderMapDisposition providerMapDisposition,
        InstalledModelMutationLease? lease,
        Guid? activeOperationId)
    {
        Identity = identity;
        Disposition = disposition;
        ProviderMapDisposition = providerMapDisposition;
        _lease = lease;
        ActiveOperationId = activeOperationId;
    }

    public ResolvedGgufAcquisitionIdentity Identity { get; }
    public GgufAcquisitionDisposition Disposition { get; }
    public ProviderMapDisposition ProviderMapDisposition { get; }
    public Guid? ActiveOperationId { get; }
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
            await lease.DisposeAsync();
        }
    }
}
