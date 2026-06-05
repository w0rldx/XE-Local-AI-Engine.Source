namespace XE_Local_AI_Engine.Client.Services.ModelFit;

using XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Resolves an approved image id to a validated, runnable pinned image reference. This is the reusable
///     guard the scheduler model-fit handler calls before a run: it loads the descriptor, refuses to resolve a descriptor
///     that is missing, disabled, deprecated, or not sanctioned for the requested operation, and re-validates the stored
///     reference through the security validator. Rejection is a typed result, never an exception used for flow control.
/// </summary>
public interface IApprovedImageResolver
{
    /// <summary>
    ///     Resolves <paramref name="approvedImageId" /> for <paramref name="operation" />. Returns a resolution whose
    ///     <see cref="ApprovedImageResolution.IsResolved" /> is <c>true</c> only when the descriptor exists, is enabled,
    ///     not deprecated, sanctioned for the operation, and carries a valid pinned reference.
    /// </summary>
    Task<ApprovedImageResolution> ResolveAsync(string approvedImageId, ModelFitOperation operation, CancellationToken cancellationToken = default);
}

/// <summary>
///     Outcome of resolving an approved image. On success carries the validated pinned <see cref="ImageReference" /> and
///     the descriptor; on rejection carries a sanitized <see cref="RejectionReason" /> and a stable
///     <see cref="RejectionCode" /> the caller can map without parsing the message.
/// </summary>
public sealed record ApprovedImageResolution
{
    private ApprovedImageResolution(bool isResolved,
        string? imageReference,
        ApprovedUtilityImageRecord? descriptor,
        ApprovedImageRejectionCode rejectionCode,
        string? rejectionReason)
    {
        IsResolved = isResolved;
        ImageReference = imageReference;
        Descriptor = descriptor;
        RejectionCode = rejectionCode;
        RejectionReason = rejectionReason;
    }

    public bool IsResolved { get; }

    /// <summary>The validated pinned image reference (set only when resolved).</summary>
    public string? ImageReference { get; }

    /// <summary>The resolved descriptor (set only when resolved).</summary>
    public ApprovedUtilityImageRecord? Descriptor { get; }

    public ApprovedImageRejectionCode RejectionCode { get; }

    /// <summary>A sanitized operator-safe rejection reason (set only when rejected).</summary>
    public string? RejectionReason { get; }

    public static ApprovedImageResolution Resolved(string imageReference, ApprovedUtilityImageRecord descriptor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageReference);
        ArgumentNullException.ThrowIfNull(descriptor);

        return new ApprovedImageResolution(true, imageReference, descriptor, ApprovedImageRejectionCode.None, null);
    }

    public static ApprovedImageResolution Rejected(ApprovedImageRejectionCode code, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        return new ApprovedImageResolution(false, null, null, code, reason);
    }
}

/// <summary>Stable, parse-free reason an approved image resolution was rejected.</summary>
public enum ApprovedImageRejectionCode
{
    None = 0,
    NotFound = 1,
    Disabled = 2,
    Deprecated = 3,
    PurposeMismatch = 4,
    InvalidReference = 5
}
