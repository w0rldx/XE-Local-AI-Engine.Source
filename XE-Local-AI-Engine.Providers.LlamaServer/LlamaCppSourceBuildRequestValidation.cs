namespace XE_Local_AI_Engine.Providers.LlamaServer;

using System.Text.RegularExpressions;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>Strict validation and canonicalization for trusted public GitHub source-build requests.</summary>
/// <remarks>
///     <see cref="Normalize" /> is IDEMPOTENT by contract: it runs at the transport edge (the FluentValidation request
///     validator) and again inside <see cref="Contracts.ILlamaCppSourceBuildService.StartAsync" />, and callers may hand
///     an already-normalized request to either. A pass that rejected its own output would fail every official build.
/// </remarks>
public static partial class LlamaCppSourceBuildRequestValidation
{
    public const string OfficialRepository = "https://github.com/ggml-org/llama.cpp";

    public static LlamaCppSourceBuildRequest Normalize(LlamaCppSourceBuildRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.Backend) || !Enum.IsDefined(request.Source))
        {
            throw new LlamaRuntimeException("The source-build backend or source selection is invalid.");
        }

        if (request.Source == LlamaCppSourceSelection.Official)
        {
            // Only the server-selected canonical repository may accompany the official source — anything else is a
            // client attempt to override it and is rejected. Echoing the canonical value back is what keeps this
            // method idempotent (see the type remarks), because the first pass writes exactly that value.
            if (!string.IsNullOrWhiteSpace(request.Repository)
                && !string.Equals(request.Repository, OfficialRepository, StringComparison.Ordinal))
            {
                throw new LlamaRuntimeException("The official source repository is selected by the server.");
            }

            // An optional explicit commit is allowed for the official repository (the code is still upstream code).
            // Without one, the engine-pinned revision is used.
            return request with
            {
                Repository = OfficialRepository,
                Commit = NormalizeCommit(request.Commit)
            };
        }

        if (!request.AcknowledgeCustomSourceRisk)
        {
            throw new LlamaRuntimeException("Custom source builds require acknowledgement that the repository code will execute with the app user's privileges.");
        }

        return request with
        {
            Repository = NormalizeGitHubRepository(request.Repository),
            Commit = NormalizeCommit(request.Commit)
        };
    }

    public static string? NormalizeCommit(string? commit)
    {
        if (string.IsNullOrWhiteSpace(commit))
        {
            return null;
        }

        var trimmed = commit.Trim();
        if (!CommitRegex().IsMatch(trimmed))
        {
            throw new LlamaRuntimeException("The source commit must be a full 40-character hexadecimal SHA.");
        }

        return Convert.ToHexStringLower(Convert.FromHexString(trimmed));
    }

    public static string NormalizeGitHubRepository(string? repository)
    {
        if (string.IsNullOrWhiteSpace(repository) || repository.Any(char.IsControl))
        {
            throw new LlamaRuntimeException("A canonical public GitHub HTTPS repository is required.");
        }

        if (repository.StartsWith("https://github.com:", StringComparison.OrdinalIgnoreCase)
            || !Uri.TryCreate(repository, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
            || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new LlamaRuntimeException("A canonical public GitHub HTTPS repository is required.");
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2 || !RepositorySegmentRegex().IsMatch(segments[0]) || !RepositorySegmentRegex().IsMatch(segments[1]))
        {
            throw new LlamaRuntimeException("The GitHub repository path must contain exactly an owner and repository name.");
        }

        var repo = segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? segments[1][..^4] : segments[1];
        if (repo.Length == 0)
        {
            throw new LlamaRuntimeException("The GitHub repository name is invalid.");
        }

        return $"https://github.com/{segments[0]}/{repo}";
    }

    public static bool HasValidOfficialProvenance(LlamaCppSourceSelection source,
        string? repository,
        LlamaCppSourceRevisionMode? revisionMode,
        string? requestedCommit,
        string? resolvedCommit)
    {
        if (source != LlamaCppSourceSelection.Official)
        {
            return true;
        }

        if (!string.Equals(repository, OfficialRepository, StringComparison.Ordinal))
        {
            return false;
        }

        return revisionMode switch
        {
            LlamaCppSourceRevisionMode.EnginePinned =>
                requestedCommit is null
                && string.Equals(resolvedCommit, LlamaCppReleasePins.PinnedSourceCommitSha, StringComparison.OrdinalIgnoreCase),
            LlamaCppSourceRevisionMode.ExplicitCommit =>
                requestedCommit is { Length: 40 }
                && requestedCommit.All(Uri.IsHexDigit)
                && string.Equals(resolvedCommit, requestedCommit, StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    [GeneratedRegex("^[0-9a-fA-F]{40}$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
    private static partial Regex CommitRegex();

    [GeneratedRegex("^[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
    private static partial Regex RepositorySegmentRegex();
}
