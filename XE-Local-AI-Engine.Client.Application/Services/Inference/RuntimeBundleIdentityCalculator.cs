namespace XE_Local_AI_Engine.Client.Services.Inference;

using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

/// <summary>One file of the selected llama.cpp runtime bundle. Names only — never a path, so the facts stay shareable.</summary>
public sealed record RuntimeBundleFileFactsV1(string Name, long SizeBytes, long LastWriteUtcTicks);

/// <summary>
///     The identity of the directory the selected <c>llama-server</c> runs out of: the executable plus every sibling
///     shared library, hashed as one document. Produced by <see cref="RuntimeBundleIdentityCalculator" /> and reused
///     verbatim by <see cref="LaunchPolicyFingerprintProvider" /> (as a fingerprint member) and by the benchmark
///     environment facts.
/// </summary>
public sealed record RuntimeBundleFactsV1(string Identity, int FileCount, IReadOnlyList<RuntimeBundleFileFactsV1> Files);

/// <summary>
///     The one implementation of the runtime-bundle identity hash. Extracted from
///     <see cref="LaunchPolicyFingerprintProvider" /> so the benchmark environment facts record the same value the
///     launch-policy fingerprint commits to — the hashed byte stream (name length, name, file length, content
///     identity, in ordinal name order) is unchanged by the extraction and must stay that way: any edit here
///     invalidates every persisted fingerprint.
/// </summary>
internal static class RuntimeBundleIdentityCalculator
{
    /// <param name="contentIdentityResolver">
    ///     Per-file content identity as lowercase hex — the caller chooses the strong SHA-256 or the cheap validation
    ///     stamp (<see cref="GetFileValidationIdentityAsync" />).
    /// </param>
    internal static async Task<RuntimeBundleFactsV1> ComputeAsync(string serverExecutablePath,
        Func<string, CancellationToken, Task<string>> contentIdentityResolver,
        CancellationToken ct)
    {
        var executablePath = Path.GetFullPath(serverExecutablePath);
        var directory = Path.GetDirectoryName(executablePath)
                        ?? throw new InvalidOperationException("The selected llama-server path has no parent directory.");
        var files = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                             .Where(path => IsRuntimeBundleFile(path, executablePath))
                             .OrderBy(Path.GetFileName, StringComparer.Ordinal)
                             .ToArray();
        if (files.Length == 0)
        {
            throw new FileNotFoundException("The selected llama-server runtime bundle no longer exists.", executablePath);
        }

        var listing = new List<RuntimeBundleFileFactsV1>(files.Length);
        using var bundleHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in files)
        {
            ct.ThrowIfCancellationRequested();
            var nameBytes = Encoding.UTF8.GetBytes(Path.GetFileName(path));
            var nameLength = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(nameLength, nameBytes.Length);
            var fileLength = new byte[sizeof(long)];
            var file = new FileInfo(path);
            file.Refresh();
            BinaryPrimitives.WriteInt64LittleEndian(fileLength, file.Length);
            bundleHash.AppendData(nameLength);
            bundleHash.AppendData(nameBytes);
            bundleHash.AppendData(fileLength);
            var contentIdentity = await contentIdentityResolver(path, ct).ConfigureAwait(false);
            bundleHash.AppendData(Convert.FromHexString(contentIdentity));
            listing.Add(new RuntimeBundleFileFactsV1(Path.GetFileName(path), file.Length, file.LastWriteTimeUtc.Ticks));
        }

        return new RuntimeBundleFactsV1(Convert.ToHexStringLower(bundleHash.GetHashAndReset()), files.Length, listing);
    }

    /// <summary>The cheap per-file identity: metadata plus the guard samples, never a whole-file read.</summary>
    internal static async Task<string> GetFileValidationIdentityAsync(string filePath,
        LaunchPolicyFileHashCache hashCache,
        CancellationToken ct)
    {
        var file = new FileInfo(filePath);
        file.Refresh();
        if (!file.Exists)
        {
            throw new FileNotFoundException("The fingerprinted file no longer exists.", filePath);
        }

        var guard = await hashCache.GetGuardSha256Async(file.FullName, ct).ConfigureAwait(false);
        return BuildValidationIdentity(file, guard, authoritySha256: null);
    }

    internal static string BuildValidationIdentity(FileInfo file, string guardSha256, string? authoritySha256)
    {
        var canonical = string.Create(CultureInfo.InvariantCulture,
            $"{file.Length}:{file.LastWriteTimeUtc.Ticks}:{file.CreationTimeUtc.Ticks}:{guardSha256}:{authoritySha256 ?? "unavailable"}");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static bool IsRuntimeBundleFile(string path, string executablePath)
    {
        if (string.Equals(Path.GetFullPath(path), executablePath, StringComparison.Ordinal))
        {
            return true;
        }

        var name = Path.GetFileName(path);
        return name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase)
               || name.Contains(".so", StringComparison.OrdinalIgnoreCase);
    }
}
