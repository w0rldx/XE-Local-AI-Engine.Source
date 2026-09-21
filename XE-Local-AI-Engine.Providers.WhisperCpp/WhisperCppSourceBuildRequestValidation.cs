namespace XE_Local_AI_Engine.Providers.WhisperCpp;

using System.Text.RegularExpressions;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;

/// <summary>
///     Strict, idempotent validation for managed source-build requests: only a canonical public GitHub HTTPS repository
///     and a full 40-character hexadecimal commit are accepted.
/// </summary>
/// <remarks>
///     The official source is pinned by the server rather than trusted from the caller. <see cref="Normalize" /> is idempotent by
///     construction: normalizing an already-normalized request returns an equal request. That matters because the installed-runtime
///     store validates a persisted repository by round-tripping it through <see cref="NormalizeGitHubRepository" /> and comparing, so a
///     normalizer that changed its answer on the second pass would tombstone every healthy record.
/// </remarks>
public static partial class WhisperCppSourceBuildRequestValidation
{
    /// <summary>The official upstream repository. The only value the official source selection may resolve to.</summary>
    public const string OfficialRepository = "https://github.com/ggml-org/whisper.cpp";

    /// <summary>Validates and canonicalizes a source-build request.</summary>
    /// <exception cref="WhisperRuntimeException">The request is not a valid public-GitHub build request.</exception>
    public static WhisperCppSourceBuildRequest Normalize(WhisperCppSourceBuildRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.Backend) || !Enum.IsDefined(request.Source))
        {
            throw new WhisperRuntimeException("The source-build backend or source selection is invalid.");
        }

        if (request.Source == WhisperCppSourceSelection.Official)
        {
            if (!string.IsNullOrWhiteSpace(request.Repository)
                && !string.Equals(request.Repository, OfficialRepository, StringComparison.Ordinal))
            {
                throw new WhisperRuntimeException("The official source repository is selected by the server.");
            }

            if (!string.IsNullOrWhiteSpace(request.Commit))
            {
                throw new WhisperRuntimeException("The official source uses the engine-pinned revision; select custom source to build a specific commit.");
            }

            return request with
            {
                Repository = OfficialRepository,
                Commit = null
            };
        }

        if (!request.AcknowledgeCustomSourceRisk)
        {
            throw new WhisperRuntimeException("Custom source builds require acknowledgement that repository code executes with the app user's privileges.");
        }

        return request with
        {
            Repository = NormalizeGitHubRepository(request.Repository),
            Commit = NormalizeCommit(request.Commit)
        };
    }

    /// <summary>Canonicalizes a commit SHA to lowercase hex, or returns <see langword="null" /> for a blank one.</summary>
    /// <exception cref="WhisperRuntimeException">The value is present but is not a full 40-character hexadecimal SHA.</exception>
    public static string? NormalizeCommit(string? commit)
    {
        if (string.IsNullOrWhiteSpace(commit))
        {
            return null;
        }

        var trimmed = commit.Trim();
        if (!CommitRegex().IsMatch(trimmed))
        {
            throw new WhisperRuntimeException("The source commit must be a full 40-character hexadecimal SHA.");
        }

        return Convert.ToHexStringLower(Convert.FromHexString(trimmed));
    }

    /// <summary>Canonicalizes a public GitHub HTTPS repository URL to <c>https://github.com/{owner}/{repo}</c>.</summary>
    /// <exception cref="WhisperRuntimeException">The value is not a canonical public GitHub HTTPS repository.</exception>
    public static string NormalizeGitHubRepository(string? repository)
    {
        if (string.IsNullOrWhiteSpace(repository)
            || repository.Any(char.IsControl)
            || !Uri.TryCreate(repository, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
            || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new WhisperRuntimeException("A canonical public GitHub HTTPS repository is required.");
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2 || !RepositorySegmentRegex().IsMatch(segments[0]) || !RepositorySegmentRegex().IsMatch(segments[1]))
        {
            throw new WhisperRuntimeException("The GitHub repository path must contain exactly an owner and repository name.");
        }

        var repo = segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? segments[1][..^4] : segments[1];
        if (repo.Length == 0)
        {
            throw new WhisperRuntimeException("The GitHub repository name is invalid.");
        }

        return $"https://github.com/{segments[0]}/{repo}";
    }

    [GeneratedRegex("^[0-9a-fA-F]{40}$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
    private static partial Regex CommitRegex();

    [GeneratedRegex("^[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
    private static partial Regex RepositorySegmentRegex();
}
