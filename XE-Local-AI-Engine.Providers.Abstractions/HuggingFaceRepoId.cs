namespace XE_Local_AI_Engine.Providers.Abstractions;

using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

/// <summary>
///     The one shape check for a Hugging Face repository id (<c>owner/name</c>). The id is spliced into Hub URL paths
///     that carry the bearer token, so a <c>?</c>, <c>#</c>, <c>@</c> or <c>..</c> must never reach a URL builder.
/// </summary>
public static partial class HuggingFaceRepoId
{
    public const string InvalidMessage = "The repository id must look like 'owner/name' (letters, digits, '_', '.', '-').";

    public static bool IsValid([NotNullWhen(true)] string? repoId) =>
        repoId is not null && !repoId.Contains("..", StringComparison.Ordinal) && Shape().IsMatch(repoId);

    /// <summary>Returns the id escaped per path segment for a URL, after rejecting any id that fails <see cref="IsValid" />.</summary>
    public static string EscapePath(string repoId)
    {
        if (!IsValid(repoId))
        {
            throw new ArgumentException(InvalidMessage, nameof(repoId));
        }

        var slash = repoId.IndexOf('/', StringComparison.Ordinal);
        return Uri.EscapeDataString(repoId[..slash]) + "/" + Uri.EscapeDataString(repoId[(slash + 1)..]);
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]*/[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex Shape();
}
