namespace XE_Local_AI_Engine.Providers.Abstractions;

using System.Security.Cryptography;

/// <summary>The lowercase hex SHA-256 of a file's content, the form every recorded digest in the node uses.</summary>
public static class FileSha256
{
    public static async Task<string> ComputeAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Whether the file's SHA-256 equals <paramref name="expectedSha256" />, compared case-insensitively.</summary>
    public static async Task<bool> MatchesAsync(string path, string expectedSha256, CancellationToken cancellationToken) =>
        string.Equals(await ComputeAsync(path, cancellationToken).ConfigureAwait(false), expectedSha256, StringComparison.OrdinalIgnoreCase);
}
