namespace XE_Local_AI_Engine.Client.Services.Development;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
///     One command the Development catalog can run, fully materialized: the executable, the exact argument vector, and
///     the wall-clock budget for this command alone.
///     <para>
///         The arguments are materialized (the build target is already substituted) rather than templated, so the
///         canonical digest of the owning profile describes exactly what will execute. A templated form would let two
///         profiles with identical digests run different commands.
///     </para>
/// </summary>
internal sealed record DevelopmentProfileCommand(
    string CommandId,
    string Executable,
    IReadOnlyList<string> Arguments,
    int TimeoutSeconds);

/// <summary>
///     The per-project command profile: which commands exist for this repository, what each one runs, which of them the
///     deterministic validation gate executes and in what order, and which paths the test-write policy protects.
///     <para>
///         This replaces the former <c>Solution</c> constant and the hardcoded <c>ExecuteCatalogAsync</c> switch, which
///         named <c>XE-Local-AI-Engine.slnx</c> literally and therefore made Dev Mode able to build and test exactly one
///         repository while advertising that it could bind any.
///     </para>
///     <para>
///         The profile is snapshotted into the database at project creation and is the only source of truth thereafter.
///         The worktree copy at <c>.xe-dev/profile.json</c> is an import source, never read during an attempt — the agent
///         can write to the worktree, so a live read would let it rewrite its own test command to <c>true</c>.
///     </para>
/// </summary>
internal sealed record DevelopmentCommandProfile(
    string ProfileId,
    string ProfileVersion,
    string? TemplateId,
    string? BuildTarget,

    /// <summary>
    ///     SHA-256 of the raw <c>.xe-dev/profile.json</c> bytes this profile was imported from, or null when the
    ///     repository shipped no such file. Provenance: it records which declaration the operator confirmed, and it
    ///     participates in the canonical digest so re-importing a changed declaration yields a different profile.
    ///     <para>
    ///         It is deliberately NOT what the per-attempt tamper check compares against. This value comes from the
    ///         operator's live repository working tree at project creation, while the managed worktree is checked out
    ///         at the attempt's base commit, so the two legitimately differ whenever the file has an uncommitted edit.
    ///         The tamper check captures its own baseline from the worktree at attempt start — see
    ///         <c>DevelopmentWorkspaceTools</c>.
    ///     </para>
    /// </summary>
    string? ImportDigest,
    IReadOnlyList<DevelopmentProfileCommand> Commands,
    IReadOnlyList<string> ValidationCommandIds,
    IReadOnlyList<string> ProtectedPaths,
    bool IsCustom)
{
    /// <summary>
    ///     The canonical UTF-8 JSON form. Property order is written explicitly and list order is preserved, so the bytes
    ///     are stable for a given profile value and can be hashed. Do not switch this to reflection-based serialization:
    ///     the digest is a security boundary, and property ordering would then depend on member declaration order.
    /// </summary>
    public byte[] ToCanonicalUtf8()
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteString("profileId", ProfileId);
            writer.WriteString("profileVersion", ProfileVersion);
            if (TemplateId is null)
            {
                writer.WriteNull("templateId");
            }
            else
            {
                writer.WriteString("templateId", TemplateId);
            }

            if (BuildTarget is null)
            {
                writer.WriteNull("buildTarget");
            }
            else
            {
                writer.WriteString("buildTarget", BuildTarget);
            }

            if (ImportDigest is null)
            {
                writer.WriteNull("importDigest");
            }
            else
            {
                writer.WriteString("importDigest", ImportDigest);
            }

            writer.WriteStartArray("commands");
            foreach (var command in Commands)
            {
                writer.WriteStartObject();
                writer.WriteString("commandId", command.CommandId);
                writer.WriteString("executable", command.Executable);
                writer.WriteStartArray("arguments");
                foreach (var argument in command.Arguments)
                {
                    writer.WriteStringValue(argument);
                }

                writer.WriteEndArray();
                writer.WriteNumber("timeoutSeconds", command.TimeoutSeconds);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            writer.WriteStartArray("validationCommandIds");
            foreach (var commandId in ValidationCommandIds)
            {
                writer.WriteStringValue(commandId);
            }

            writer.WriteEndArray();

            writer.WriteStartArray("protectedPaths");
            foreach (var pattern in ProtectedPaths)
            {
                writer.WriteStringValue(pattern);
            }

            writer.WriteEndArray();
            writer.WriteBoolean("isCustom", IsCustom);
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    /// <summary>
    ///     Lowercase hex SHA-256 over <see cref="ToCanonicalUtf8" />. 64 characters, which is exactly the width of the
    ///     existing <c>command_profile_version</c> column — deliberately a separate column, because that one carries an
    ///     artifact <em>protocol</em> version and the two must not share storage.
    /// </summary>
    public string ComputeDigest() => Convert.ToHexStringLower(SHA256.HashData(ToCanonicalUtf8()));

    public DevelopmentProfileCommand ResolveCommand(string commandId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);
        return Commands.FirstOrDefault(command => string.Equals(command.CommandId, commandId, StringComparison.Ordinal))
               ?? throw new DevelopmentWorkspaceSecurityException(
                   "The requested command id is not in the resolved Development command profile.");
    }

    /// <summary>
    ///     True when the repository-relative path matches any protected test pattern. Paths arrive from
    ///     <c>git diff --name-status</c>, so they are always repository-relative with forward slashes.
    /// </summary>
    public bool IsProtectedTestPath(string repositoryRelativePath)
    {
        if (string.IsNullOrWhiteSpace(repositoryRelativePath))
        {
            return false;
        }

        var normalized = repositoryRelativePath.Replace('\\', '/').TrimStart('/');
        return ProtectedPaths.Any(pattern => DevelopmentGlob.IsMatch(pattern, normalized));
    }

    public static DevelopmentCommandProfile FromCanonicalJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var profile = JsonSerializer.Deserialize<DevelopmentCommandProfile>(json, CanonicalReadOptions)
                      ?? throw new DevelopmentWorkspaceSecurityException("The stored Development command profile is not readable.");
        return profile.Validated();
    }

    /// <summary>
    ///     Structural validation applied to every profile before it is trusted, whether it came from the code-owned
    ///     catalog, the database, or an import. A profile whose validation list names a command it does not define would
    ///     otherwise fail deep inside an attempt instead of at resolution.
    /// </summary>
    public DevelopmentCommandProfile Validated()
    {
        if (string.IsNullOrWhiteSpace(ProfileId) || string.IsNullOrWhiteSpace(ProfileVersion))
        {
            throw new DevelopmentWorkspaceSecurityException("A Development command profile requires an id and a version.");
        }

        if (Commands.Count == 0)
        {
            throw new DevelopmentWorkspaceSecurityException("A Development command profile defines no commands.");
        }

        var duplicate = Commands.GroupBy(command => command.CommandId, StringComparer.Ordinal)
                                .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new DevelopmentWorkspaceSecurityException("A Development command profile defines the same command id twice.");
        }

        foreach (var command in Commands)
        {
            if (string.IsNullOrWhiteSpace(command.CommandId) || string.IsNullOrWhiteSpace(command.Executable))
            {
                throw new DevelopmentWorkspaceSecurityException("A Development profile command requires an id and an executable.");
            }

            if (command.TimeoutSeconds <= 0)
            {
                throw new DevelopmentWorkspaceSecurityException("A Development profile command requires a positive timeout.");
            }
        }

        // The engine itself routes through these two: GetStatusAsync is the coder's status tool, and the whitespace
        // check is the one command every profile's validation list is expected to be able to include. A profile that
        // omitted them would compile and then fail deep inside an attempt, so require them at resolution instead.
        string[] required = [DevelopmentCommandIds.GitStatus, DevelopmentCommandIds.GitDiffCheck];
        if (required.Any(id => !Commands.Any(command => string.Equals(command.CommandId, id, StringComparison.Ordinal))))
        {
            throw new DevelopmentWorkspaceSecurityException(
                "A Development command profile must define the engine's baseline git commands.");
        }

        if (ValidationCommandIds.Count == 0)
        {
            throw new DevelopmentWorkspaceSecurityException("A Development command profile defines no validation commands.");
        }

        if (ValidationCommandIds.Any(commandId =>
                !Commands.Any(command => string.Equals(command.CommandId, commandId, StringComparison.Ordinal))))
        {
            throw new DevelopmentWorkspaceSecurityException(
                "A Development command profile validates a command it does not define.");
        }

        return this;
    }

    private static readonly JsonSerializerOptions CanonicalReadOptions = new(JsonSerializerDefaults.Web);
}

/// <summary>
///     A deliberately small glob matcher for the profile's protected-path patterns.
///     <para>
///         Hand-rolled rather than taken from <c>Microsoft.Extensions.FileSystemGlobbing</c> for two reasons: that
///         package is not referenced by this assembly and would need a Central Package Management entry, and the matcher
///         participates in a security decision whose behaviour must not drift with a transitive package bump.
///     </para>
///     <para>
///         Supported syntax: <c>**</c> spans any number of path segments, <c>*</c> matches within one segment, and
///         <c>?</c> matches one non-separator character. Matching is case-insensitive so that a rename to
///         <c>featuretests.cs</c> on a case-sensitive filesystem cannot escape the policy.
///     </para>
/// </summary>
internal static class DevelopmentGlob
{
    private static readonly Dictionary<string, Regex> Cache = [];
    private static readonly Lock CacheGate = new();

    public static bool IsMatch(string pattern, string path)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        Regex regex;
        lock (CacheGate)
        {
            if (!Cache.TryGetValue(pattern, out regex!))
            {
                regex = new Regex(Translate(pattern),
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(250));
                Cache[pattern] = regex;
            }
        }

        return regex.IsMatch(path);
    }

    private static string Translate(string pattern)
    {
        var builder = new StringBuilder("^");
        var index = 0;
        while (index < pattern.Length)
        {
            var current = pattern[index];
            if (current != '*')
            {
                _ = current switch
                {
                    '?' => builder.Append("[^/]"),
                    _ => builder.Append(Regex.Escape(current.ToString()))
                };
                index++;
                continue;
            }

            if (index + 1 < pattern.Length && pattern[index + 1] == '*')
            {
                if (index + 2 < pattern.Length && pattern[index + 2] == '/')
                {
                    // "**/" spans zero or more whole segments, so "**/*Tests.cs" also matches a root-level file.
                    _ = builder.Append("(?:[^/]+/)*");
                    index += 3;
                }
                else
                {
                    _ = builder.Append(".*");
                    index += 2;
                }

                continue;
            }

            _ = builder.Append("[^/]*");
            index++;
        }

        return builder.Append('$').ToString();
    }
}
