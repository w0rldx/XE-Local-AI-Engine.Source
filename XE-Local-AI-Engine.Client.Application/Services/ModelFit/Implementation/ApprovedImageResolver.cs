namespace XE_Local_AI_Engine.Client.Services.ModelFit.Implementation;

using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.ModelFit.Validation;

/// <summary>
///     Default <see cref="IApprovedImageResolver" />: loads the descriptor from the registry, applies the enable/deprecate/
///     purpose guards, and re-validates the stored pinned reference. It never logs or echoes the image
///     reference and never rewrites it — the reference must already be canonical and allowlisted.
/// </summary>
public sealed class ApprovedImageResolver : IApprovedImageResolver
{
    private readonly IApprovedUtilityImageStore _store;
    private readonly ApprovedImageReferenceValidator _referenceValidator;

    public ApprovedImageResolver(IApprovedUtilityImageStore store, ApprovedImageReferenceValidator referenceValidator)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _referenceValidator = referenceValidator ?? throw new ArgumentNullException(nameof(referenceValidator));
    }

    public async Task<ApprovedImageResolution> ResolveAsync(string approvedImageId, ModelFitOperation operation, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(approvedImageId))
        {
            return ApprovedImageResolution.Rejected(ApprovedImageRejectionCode.NotFound, "Approved image id is required.");
        }

        var descriptor = await _store.GetByIdAsync(approvedImageId, cancellationToken).ConfigureAwait(false);
        if (descriptor is null)
        {
            return ApprovedImageResolution.Rejected(ApprovedImageRejectionCode.NotFound, "The approved image was not found.");
        }

        if (!descriptor.Enabled)
        {
            return ApprovedImageResolution.Rejected(ApprovedImageRejectionCode.Disabled, "The approved image is disabled.");
        }

        if (descriptor.DeprecatedAtUtc is not null)
        {
            return ApprovedImageResolution.Rejected(ApprovedImageRejectionCode.Deprecated, "The approved image is deprecated.");
        }

        var requiredPurpose = ToRequiredPurpose(operation);
        if (!descriptor.Purpose.HasFlag(requiredPurpose))
        {
            return ApprovedImageResolution.Rejected(ApprovedImageRejectionCode.PurposeMismatch, "The approved image is not sanctioned for the requested operation.");
        }

        if (!_referenceValidator.IsValid(descriptor.ImageReference))
        {
            // A descriptor whose stored reference fails validation must never run — the registry can only be seeded with
            // valid references, so this is defense in depth against drift. The reference itself is never echoed.
            return ApprovedImageResolution.Rejected(ApprovedImageRejectionCode.InvalidReference, "The approved image reference failed validation.");
        }

        return ApprovedImageResolution.Resolved(descriptor.ImageReference, descriptor);
    }

    private static UtilityImagePurpose ToRequiredPurpose(ModelFitOperation operation)
    {
        return operation switch
        {
            ModelFitOperation.Recommend => UtilityImagePurpose.ModelRecommendation,
            ModelFitOperation.Benchmark => UtilityImagePurpose.ModelBenchmark,
            _ => UtilityImagePurpose.None
        };
    }
}
