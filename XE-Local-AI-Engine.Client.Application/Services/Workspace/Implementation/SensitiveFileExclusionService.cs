namespace XE_Local_AI_Engine.Client.Services.Workspace.Implementation;

using System.Collections.Frozen;

/// <summary>
///     Name-based <see cref="ISensitiveFileExclusionService" />, built from two deliberately separate sets.
///     <para>
///         <see cref="SecretNames" /> plus <see cref="SecretFileGlobs" /> answer "is this a CREDENTIAL?" and are what a
///         read path gates on. <see cref="CopySkipNames" /> answers the different question "is this worth COPYING?" and
///         names the host <c>.git</c> baseline (a fresh in-sandbox baseline is created after copy) plus generated/heavy
///         output directories. The copy filter uses the union; nothing else should.
///     </para>
///     <para>
///         Keeping them apart is the point. Folding build output into a read gate refuses
///         <c>obj/project.assets.json</c> to an agent diagnosing a failed restore — the exact thing the feature exists
///         to do — while protecting nothing, because generated output is not a credential.
///     </para>
///     <para>
///         The literal sets are frozen once so the common lookup is an allocation-free hash probe; only a name that
///         misses them is walked against the wildcard rules.
///     </para>
/// </summary>
internal sealed class SensitiveFileExclusionService : ISensitiveFileExclusionService
{
    // CREDENTIAL-BEARING literal names. This set gates reads, so an addition here denies an agent a file — include a
    // name only when its contents are a secret, never merely because it is noisy or large.
    private static readonly FrozenSet<string> SecretNames = new[]
    {
        ".ssh",
        ".env",
        "secrets.json",
        "appsettings.Production.json",
        "cloud-credentials.enc",
        "worker-credentials.enc",

        // This product's own operator secret. node.key is the 32-byte root from which the SQLite column key, the node
        // JWT signing key and the Data Protection key-ring KEK are all derived, so it decrypts every .enc blob beside
        // it — it is the single highest-value file the node ever writes.
        "node.key",

        // Credential stores a developer's home directory and repositories routinely carry.
        ".netrc",
        ".npmrc",
        ".git-credentials",
        ".aws",
        ".kube",
        ".docker"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    // NOT secrets — skipped by the workspace copy because they are generated, heavy, or rebuilt in the sandbox. A read
    // path must NOT gate on these: build output is exactly what an agent needs after a failed build.
    private static readonly FrozenSet<string> CopySkipNames = new[]
    {
        ".git",
        "bin",
        "obj",
        "node_modules",
        "dist",
        "coverage",
        ".vs",
        ".idea"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    // The wildcard SECRET rules, written as grep/find-compatible globs. Every entry is either "prefix*" or "*suffix" —
    // one wildcard, at one end — which is what MatchesGlob below implements. This array is the ONLY definition of the
    // pattern rules: the flag sets and both predicates read it, so a pattern can no longer be added to one and
    // forgotten in the other.
    private static readonly string[] SecretFileGlobs =
    [
        // ".env.local", ".env.production", … (the ".env" base name is already in SecretNames).
        ".env.*",

        // Every at-rest encrypted secret this product writes: cloud/worker credentials, hf-token.enc,
        // github-token.enc, codex-oauth-tokens.enc, entra-*.enc — and any future one, without a new rule here.
        "*.enc",

        // The node database and its WAL/SHM sidecars: chat history, agent state, and every encrypted column.
        "node.sqlite*",

        // Private keys and certificate bundles.
        "*.pem",
        "*.pfx",
        "*.p12",
        "id_rsa*",
        "id_ed25519*"
    ];

    // The copy filter's literal set is exactly the union, so the copy behaviour is unchanged by the split.
    private static readonly FrozenSet<string> ExcludedNames =
        SecretNames.Concat(CopySkipNames).ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    // Flag sets for grep/find. Derived from the same arrays the predicates use, so a name can never be advertised as a
    // flag without the matching predicate enforcing it.
    private static readonly IReadOnlyList<string> ExcludedNamePatterns = [.. ExcludedNames, .. SecretFileGlobs];
    private static readonly IReadOnlyList<string> SecretNamePatterns = [.. SecretNames, .. SecretFileGlobs];

    public IReadOnlyList<string> ExcludedEntryNames => ExcludedNamePatterns;

    public IReadOnlyList<string> SecretEntryNames => SecretNamePatterns;

    public bool IsExcluded(string entryName, bool isDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryName);

        if (ExcludedNames.Contains(entryName))
        {
            return true;
        }

        // Pattern rules apply to files only; a directory matching a file pattern is unusual and copied through.
        return !isDirectory && IsSecretFilePattern(entryName);
    }

    public bool IsSecret(string entryName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryName);

        // No isDirectory parameter on purpose: a read path resolves a path SEGMENT and cannot always know whether the
        // segment is a directory, and ".ssh"/".aws" must deny either way.
        return SecretNames.Contains(entryName) || IsSecretFilePattern(entryName);
    }

    private static bool IsSecretFilePattern(string fileName)
    {
        return Array.Exists(SecretFileGlobs, glob => MatchesGlob(fileName, glob));
    }

    private static bool MatchesGlob(string fileName, string glob)
    {
        if (glob.StartsWith('*'))
        {
            return fileName.EndsWith(glob[1..], StringComparison.OrdinalIgnoreCase);
        }

        if (glob.EndsWith('*'))
        {
            return fileName.StartsWith(glob[..^1], StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(fileName, glob, StringComparison.OrdinalIgnoreCase);
    }
}
