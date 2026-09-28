namespace XE_Local_AI_Engine.Client.Services.Models;

using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using XE_Local_AI_Engine.Client.Services.Validation;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

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
