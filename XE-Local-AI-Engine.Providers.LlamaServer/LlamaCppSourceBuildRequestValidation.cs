namespace XE_Local_AI_Engine.Providers.LlamaServer;

using System.Text.RegularExpressions;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>Strict validation and canonicalization for trusted public GitHub source-build requests.</summary>
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

        var commit = NormalizeCommit(request.Commit);
        if (request.Source == LlamaCppSourceSelection.Official)
        {
            if (!string.IsNullOrWhiteSpace(request.Repository))
            {
                throw new LlamaRuntimeException("The official source repository is selected by the server.");
            }

            return request with { Repository = OfficialRepository, Commit = commit };
        }

        if (!request.AcknowledgeCustomSourceRisk)
        {
            throw new LlamaRuntimeException("Custom source builds require acknowledgement that the repository code will execute with the app user's privileges.");
        }

        return request with { Repository = NormalizeGitHubRepository(request.Repository), Commit = commit };
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

    [GeneratedRegex("^[0-9a-fA-F]{40}$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
    private static partial Regex CommitRegex();

    [GeneratedRegex("^[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
    private static partial Regex RepositorySegmentRegex();
}
