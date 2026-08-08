namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp;

using System.Text.RegularExpressions;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

/// <summary>Strict, idempotent validation for public GitHub source-build requests.</summary>
public static partial class StableDiffusionCppSourceBuildRequestValidation
{
    public const string OfficialRepository = "https://github.com/leejet/stable-diffusion.cpp";

    public static StableDiffusionCppSourceBuildRequest Normalize(StableDiffusionCppSourceBuildRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.Backend) || !Enum.IsDefined(request.Source))
        {
            throw new StableDiffusionRuntimeException("The source-build backend or source selection is invalid.");
        }

        if (request.Source == StableDiffusionCppSourceSelection.Official)
        {
            if (!string.IsNullOrWhiteSpace(request.Repository)
                && !string.Equals(request.Repository, OfficialRepository, StringComparison.Ordinal))
            {
                throw new StableDiffusionRuntimeException("The official source repository is selected by the server.");
            }

            if (!string.IsNullOrWhiteSpace(request.Commit))
            {
                throw new StableDiffusionRuntimeException("The official source uses the engine-pinned revision; select custom source to build a specific commit.");
            }

            return request with
            {
                Repository = OfficialRepository,
                Commit = null
            };
        }

        if (!request.AcknowledgeCustomSourceRisk)
        {
            throw new StableDiffusionRuntimeException("Custom source builds require acknowledgement that repository code executes with the app user's privileges.");
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
            throw new StableDiffusionRuntimeException("The source commit must be a full 40-character hexadecimal SHA.");
        }

        return Convert.ToHexStringLower(Convert.FromHexString(trimmed));
    }

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
            throw new StableDiffusionRuntimeException("A canonical public GitHub HTTPS repository is required.");
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2 || !RepositorySegmentRegex().IsMatch(segments[0]) || !RepositorySegmentRegex().IsMatch(segments[1]))
        {
            throw new StableDiffusionRuntimeException("The GitHub repository path must contain exactly an owner and repository name.");
        }

        var repo = segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? segments[1][..^4] : segments[1];
        if (repo.Length == 0)
        {
            throw new StableDiffusionRuntimeException("The GitHub repository name is invalid.");
        }

        return $"https://github.com/{segments[0]}/{repo}";
    }

    [GeneratedRegex("^[0-9a-fA-F]{40}$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
    private static partial Regex CommitRegex();

    [GeneratedRegex("^[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
    private static partial Regex RepositorySegmentRegex();
}
