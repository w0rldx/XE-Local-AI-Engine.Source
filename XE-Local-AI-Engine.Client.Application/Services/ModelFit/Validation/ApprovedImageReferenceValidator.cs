namespace XE_Local_AI_Engine.Client.Services.ModelFit.Validation;

using System.Text.RegularExpressions;

/// <summary>
///     Security boundary for utility container image references. This is NOT a convenience parser: it validates that a
///     reference is ALREADY in the strict canonical form <c>&lt;repository&gt;:&lt;tag&gt;@sha256:&lt;64 lowercase hex&gt;</c>
///     against an explicit repository allowlist, and rejects anything else. It never rewrites or normalizes an untrusted
///     reference into a trusted one (no silent lowercasing of the digest, no defaulting a missing tag) — a reference that
///     is not already canonical and allowlisted is rejected. The allowlist is injectable; it defaults to the single
///     approved llmfit repository.
/// </summary>
public sealed partial class ApprovedImageReferenceValidator
{
    /// <summary>The single approved repository sanctioned during image review. Used as the default allowlist.</summary>
    public const string DefaultAllowedRepository = "ghcr.io/alexsjones/llmfit";

    private static readonly IReadOnlySet<string> DefaultAllowedRepositories =
        new HashSet<string>(StringComparer.Ordinal)
        {
            DefaultAllowedRepository
        };

    private readonly IReadOnlySet<string> _allowedRepositories;

    /// <summary>Creates a validator over the default repository allowlist (the approved llmfit repository only).</summary>
    public ApprovedImageReferenceValidator() : this(DefaultAllowedRepositories)
    {
    }

    /// <summary>Creates a validator over an explicit repository allowlist. Repositories are matched ordinally and exactly.</summary>
    public ApprovedImageReferenceValidator(IReadOnlySet<string> allowedRepositories)
    {
        ArgumentNullException.ThrowIfNull(allowedRepositories);

        _allowedRepositories = allowedRepositories;
    }

    /// <summary>
    ///     Validates <paramref name="imageReference" /> strictly against the canonical form and the allowlist. Returns a
    ///     result that is valid only when the reference is already canonical and its repository is allowlisted; otherwise
    ///     the result carries a specific <see cref="ImageReferenceValidationResult.Error" /> and the parsed parts that were
    ///     recoverable.
    /// </summary>
    public ImageReferenceValidationResult Validate(string? imageReference)
    {
        if (imageReference is null)
        {
            return ImageReferenceValidationResult.Invalid("Image reference is required.");
        }

        if (imageReference.Length == 0)
        {
            return ImageReferenceValidationResult.Invalid("Image reference must not be empty.");
        }

        // Reject leading/trailing whitespace outright — never trim an untrusted reference into a trusted one.
        if (imageReference != imageReference.Trim())
        {
            return ImageReferenceValidationResult.Invalid("Image reference must not contain leading or trailing whitespace.");
        }

        // The digest is separated by exactly one '@'. More than one is a malformed reference.
        var atParts = imageReference.Split('@');
        if (atParts.Length == 1)
        {
            return ImageReferenceValidationResult.Invalid("Image reference must be pinned by digest (missing '@sha256:<digest>').");
        }

        if (atParts.Length > 2)
        {
            return ImageReferenceValidationResult.Invalid("Image reference must contain exactly one '@' separating the digest.");
        }

        var repositoryAndTag = atParts[0];
        var digest = atParts[1];

        // The tag is separated from the repository by the LAST ':' before the '@'. The repository itself may contain a
        // ':' only as a registry port (e.g. host:5000/repo); to keep parsing strict we require a ':' after the final
        // '/' so a tag is always present.
        var lastSlashIndex = repositoryAndTag.LastIndexOf('/');
        var tagColonIndex = repositoryAndTag.IndexOf(':', lastSlashIndex + 1);
        if (tagColonIndex < 0)
        {
            return ImageReferenceValidationResult.Invalid("Image reference must include a tag (digest-only references are rejected).");
        }

        var repository = repositoryAndTag[..tagColonIndex];
        var tag = repositoryAndTag[(tagColonIndex + 1)..];

        if (repository.Length == 0)
        {
            return ImageReferenceValidationResult.Invalid("Image reference is missing a repository.");
        }

        if (tag.Length == 0)
        {
            return ImageReferenceValidationResult.Invalid("Image reference is missing a tag.");
        }

        if (string.Equals(tag, "latest", StringComparison.Ordinal))
        {
            return ImageReferenceValidationResult.Invalid("The 'latest' tag is not allowed; pin an explicit version tag.", repository, tag, digest);
        }

        if (!TagPattern().IsMatch(tag))
        {
            return ImageReferenceValidationResult.Invalid("Image tag is malformed.", repository, tag, digest);
        }

        if (!DigestPattern().IsMatch(digest))
        {
            // Covers a non-sha256 algorithm, the wrong hex length, and uppercase hex (rejected, never lowercased).
            return ImageReferenceValidationResult.Invalid("Image digest must be 'sha256:' followed by exactly 64 lowercase hex characters.", repository, tag, digest);
        }

        if (!_allowedRepositories.Contains(repository))
        {
            return ImageReferenceValidationResult.Invalid("Image repository is not on the approved allowlist.", repository, tag, digest);
        }

        return ImageReferenceValidationResult.Valid(repository, tag, digest);
    }

    /// <summary>Convenience predicate: <c>true</c> only when <see cref="Validate" /> returns a valid result.</summary>
    public bool IsValid(string? imageReference)
    {
        return Validate(imageReference).IsValid;
    }

    // Docker tag grammar: a leading [A-Za-z0-9_], then up to 127 more of [A-Za-z0-9._-]. Anchored with a bounded
    // quantifier, so it runs in linear time with no catastrophic-backtracking risk.
    [GeneratedRegex("^[A-Za-z0-9_][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex TagPattern();

    // Canonical digest: literal 'sha256:' then exactly 64 lowercase hex chars. Uppercase hex and other algorithms fail.
    [GeneratedRegex("^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex DigestPattern();
}

/// <summary>
///     Result of validating a utility image reference. <see cref="IsValid" /> is <c>true</c> only when the reference is
///     already canonical and allowlisted; otherwise <see cref="Error" /> names the specific failure and the parsed parts
///     are populated where they were recoverable.
/// </summary>
public sealed record ImageReferenceValidationResult(bool IsValid, string? Repository, string? Tag, string? Digest, string? Error)
{
    internal static ImageReferenceValidationResult Valid(string repository, string tag, string digest)
    {
        return new ImageReferenceValidationResult(true, repository, tag, digest, null);
    }

    internal static ImageReferenceValidationResult Invalid(string error, string? repository = null, string? tag = null, string? digest = null)
    {
        return new ImageReferenceValidationResult(false, repository, tag, digest, error);
    }
}
