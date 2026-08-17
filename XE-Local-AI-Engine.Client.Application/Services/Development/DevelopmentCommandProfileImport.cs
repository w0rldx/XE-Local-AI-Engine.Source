namespace XE_Local_AI_Engine.Client.Services.Development;

using System.Security.Cryptography;
using System.Text.Json;

/// <summary>
///     The shape a repository may ship at <c>.xe-dev/profile.json</c> to declare which code-owned profile it wants.
///     <para>
///         Deliberately minimal: a repository may name a profile and a build target, and nothing else. It
///         may <em>not</em> supply commands, executables, arguments or timeouts. Accepting those would let a repository
///         the agent can write choose what the validation gate executes, which is the whole reason the profile lives in
///         the database rather than the worktree.
///     </para>
/// </summary>
internal sealed record DevelopmentProfileImportDocument(string? ProfileId, string? BuildTarget);

/// <summary>
///     Reads the optional <c>.xe-dev/profile.json</c> import source from a trusted host repository root.
///     <para>
///         This runs exactly once, at project creation, on the operator's trusted host path — never during an attempt
///         and never through the sandbox. The value it produces is snapshotted into the database and the worktree copy
///         is irrelevant from that point on.
///     </para>
/// </summary>
internal static class DevelopmentCommandProfileImport
{
    public const string RelativePath = ".xe-dev/profile.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Bounded so a hostile repository cannot make the engine read an arbitrarily large file.</summary>
    private const int MaxImportBytes = 64 * 1024;

    /// <summary>
    ///     Returns the declared profile id and build target plus the digest of the exact bytes read, or null when the
    ///     repository ships no import file.
    /// </summary>
    public static ImportedProfile? TryRead(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        var path = Path.Combine(repositoryRoot, ".xe-dev", "profile.json");
        if (!File.Exists(path))
        {
            return null;
        }

        var bytes = ReadBounded(path);
        DevelopmentProfileImportDocument document;
        try
        {
            document = JsonSerializer.Deserialize<DevelopmentProfileImportDocument>(bytes, JsonOptions)
                       ?? throw new DevelopmentWorkspaceSecurityException("The repository command-profile import file is empty.");
        }
        catch (JsonException)
        {
            throw new DevelopmentWorkspaceSecurityException("The repository command-profile import file is not valid JSON.");
        }

        return new ImportedProfile(document, ComputeDigest(bytes));
    }

    /// <summary>
    ///     Digest of the import file as it currently stands in a worktree, or null when it is absent. Used by the
    ///     workspace invariant to detect a command that wrote the file as a side effect.
    /// </summary>
    public static string? TryComputeDigest(string worktreePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);
        var path = Path.Combine(worktreePath, ".xe-dev", "profile.json");
        return File.Exists(path) ? ComputeDigest(ReadBounded(path)) : null;
    }

    private static byte[] ReadBounded(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxImportBytes)
        {
            throw new DevelopmentWorkspaceSecurityException("The repository command-profile import file is too large.");
        }

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static string ComputeDigest(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>
    ///     A repository's declared command profile plus the digest of the exact bytes it was read from — the digest
    ///     rides on the resolved profile so the workspace invariant can detect a command rewriting the file mid-attempt.
    /// </summary>
    internal sealed record ImportedProfile(DevelopmentProfileImportDocument Document, string Digest);
}
