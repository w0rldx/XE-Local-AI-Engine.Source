namespace XE_Local_AI_Engine.Client.Services.Training.Runs;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Training.BaseArtifacts;

/// <summary>
///     Surfaces the base checkpoint's licensing for the run wizard's confirmation step, and turns an acknowledgement
///     into the document persisted on the run.
/// </summary>
/// <remarks>
///     A repository that declares no license is NOT a pass. The confirmation still has to happen — it just records the
///     different fact that no license metadata was found, which is exactly the case an operator most needs to see
///     before training weights they may not be allowed to redistribute.
/// </remarks>
public interface ILicenseGateService
{
    Task<TrainingLicenseGateView?> GetAsync(Guid baseArtifactId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Builds the confirmation document, hashing the exact text the operator was shown so a later reword cannot be
    ///     mistaken for consent to the same terms.
    /// </summary>
    TrainingLicenseConfirmationV1 BuildConfirmation(TrainingLicenseGateView view);
}

public sealed class LicenseGateService(ITrainingBaseArtifactStore store, TimeProvider timeProvider) : ILicenseGateService
{
    private readonly ITrainingBaseArtifactStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<TrainingLicenseGateView?> GetAsync(Guid baseArtifactId, CancellationToken cancellationToken = default)
    {
        var artifact = await _store.GetAsync(baseArtifactId, cancellationToken).ConfigureAwait(false);
        if (artifact is null)
        {
            return null;
        }

        // The absent-versus-empty distinction the store's null fix protects: no license column means the fetch never
        // found licensing metadata, which is a different statement from "the repository declares no license".
        var license = BaseArtifactManifest.DeserializeLicense(artifact.LicenseJson);
        var metadataPresent = artifact.LicenseJson.HasValue && license is not null;
        return new TrainingLicenseGateView(artifact.Id,
            license?.RepoId ?? artifact.RepoId,
            license?.License,
            license?.IsGated ?? false,
            metadataPresent,
            BuildText(license?.RepoId ?? artifact.RepoId, license?.License, license?.IsGated ?? false, metadataPresent));
    }

    public TrainingLicenseConfirmationV1 BuildConfirmation(TrainingLicenseGateView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return new TrainingLicenseConfirmationV1
        {
            RepoId = view.RepoId,
            License = view.License,
            IsGated = view.IsGated,
            MetadataPresent = view.MetadataPresent,
            ConfirmedAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            ConfirmationTextSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(view.ConfirmationText)))
        };
    }

    /// <summary>The exact wording the operator confirms. Public so the endpoint and the hash agree on one source.</summary>
    public static string BuildText(string repoId, string? license, bool isGated, bool metadataPresent)
    {
        var declaredTerms = license is { Length: > 0 } declared
            ? string.Create(CultureInfo.InvariantCulture, $"declares the license '{declared}'")
            : "declares no license";
        var terms = metadataPresent ? declaredTerms : "has no license metadata found for it";
        var gating = isGated ? " The repository is gated and its terms were accepted on Hugging Face." : string.Empty;
        return string.Create(CultureInfo.InvariantCulture,
            $"The base checkpoint '{repoId}' {terms}.{gating} I confirm I am permitted to fine-tune these weights and to use whatever this run produces.");
    }
}
