namespace XE_Local_AI_Engine.Client.Services.Development;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
///     One command the Development catalog can run, fully materialized: the executable, the exact argument vector, and
///     the wall-clock budget for this command alone.
/// </summary>
/// <remarks>
///     The arguments are materialized — the build target is already substituted — rather than templated, so the
///     canonical digest of the owning profile describes exactly what will execute; a templated form would let two
///     profiles with identical digests run different commands.
/// </remarks>
internal sealed record DevelopmentProfileCommand(
    string CommandId,
    string Executable,
    IReadOnlyList<string> Arguments,
    int TimeoutSeconds);

/// <summary>
///     The per-project command profile: which commands exist for this repository, what each one runs, which of them the
///     deterministic validation gate executes and in what order, and which paths the test-write policy protects.
/// </summary>
/// <remarks>
///     The profile is snapshotted into the database at project creation and is the only source of truth thereafter;
///     the worktree copy at <c>.xe-dev/profile.json</c> is an import source and is never read during an attempt,
///     because the agent can write to the worktree and a live read would let it rewrite its own test command to
///     <c>true</c>. No solution or repository is named in code: a hardcoded one binds Dev Mode to exactly one
///     repository while advertising that it can bind any.
/// </remarks>
internal sealed record DevelopmentCommandProfile(
    string ProfileId,
    string ProfileVersion,
    string? TemplateId,
    string? BuildTarget,
    /// <summary>
    ///     SHA-256 of the raw <c>.xe-dev/profile.json</c> bytes this profile was imported from, or null when the
    ///     repository shipped no such file.
    /// </summary>
    /// <remarks>
    ///     Provenance: it records which declaration the operator confirmed and participates in the canonical digest,
    ///     so re-importing a changed declaration yields a different profile. It is deliberately not what the
    ///     per-attempt tamper check compares against — this value comes from the operator's live working tree at
    ///     project creation while the managed worktree sits at the attempt's base commit, so an uncommitted edit
    ///     legitimately differs; that check captures its own baseline from the worktree at attempt start.
    /// </remarks>
    string? ImportDigest,
    IReadOnlyList<DevelopmentProfileCommand> Commands,
    IReadOnlyList<string> ValidationCommandIds,
    IReadOnlyList<string> ProtectedPaths,
    bool IsCustom)
{
    /// <summary>The canonical UTF-8 JSON form.</summary>
    /// <remarks>
    ///     Property order is written explicitly and list order is preserved, so the bytes are stable for a given
    ///     profile value and can be hashed. Never switch this to reflection-based serialization: the digest is a
    ///     security boundary, and property ordering would then follow member declaration order.
    /// </remarks>
    public byte[] ToCanonicalUtf8()
    {
        var buffer = new MemoryStream();
#pragma warning disable MA0045 // Utf8JsonWriter over an in-memory buffer: no I/O to await; synchronous canonical-bytes function.
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
               {
                   Indented = false
               }))
#pragma warning restore MA0045
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

    /// <summary>Lowercase hex SHA-256 over <see cref="ToCanonicalUtf8" />, 64 characters wide.</summary>
    /// <remarks>
    ///     Deliberately a column of its own rather than <c>command_profile_version</c>, which is the same width but
    ///     carries an artifact <em>protocol</em> version; the two must not share storage.
    /// </remarks>
    public string ComputeDigest() =>
        Convert.ToHexStringLower(SHA256.HashData(ToCanonicalUtf8()));

    public DevelopmentProfileCommand ResolveCommand(string commandId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);
        return Commands.FirstOrDefault(command => string.Equals(command.CommandId, commandId, StringComparison.Ordinal))
               ?? throw new DevelopmentWorkspaceSecurityException("The requested command id is not in the resolved Development command profile.");
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
    ///     Structural validation applied to every profile — catalog, database or import — before it is trusted.
    /// </summary>
    /// <remarks>
    ///     A profile whose validation list names a command it does not define would otherwise fail deep inside an
    ///     attempt instead of at resolution.
    /// </remarks>
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

        // The engine routes through these two — the coder's status tool and the whitespace check every validation list
        // may include — so require them at resolution rather than letting a profile fail deep inside an attempt.
        string[] required = [DevelopmentCommandIds.GitStatus, DevelopmentCommandIds.GitDiffCheck];
        if (required.Any(id => !Commands.Any(command => string.Equals(command.CommandId, id, StringComparison.Ordinal))))
        {
            throw new DevelopmentWorkspaceSecurityException("A Development command profile must define the engine's baseline git commands.");
        }

        if (ValidationCommandIds.Count == 0)
        {
            throw new DevelopmentWorkspaceSecurityException("A Development command profile defines no validation commands.");
        }

        if (ValidationCommandIds.Any(commandId =>
                !Commands.Any(command => string.Equals(command.CommandId, commandId, StringComparison.Ordinal))))
        {
            throw new DevelopmentWorkspaceSecurityException("A Development command profile validates a command it does not define.");
        }

        return this;
    }

    private static readonly JsonSerializerOptions CanonicalReadOptions = new(JsonSerializerDefaults.Web);
}

/// <summary>
///     A deliberately small glob matcher for the profile's protected-path patterns.
/// </summary>
/// <remarks>
///     Hand-rolled rather than taken from <c>Microsoft.Extensions.FileSystemGlobbing</c>, which this assembly does
///     not reference and which would need a Central Package Management entry, and because the matcher takes part in
///     a security decision that must not drift with a transitive package bump. <c>**</c> spans any number of path
///     segments, <c>*</c> matches within one and <c>?</c> matches one non-separator character; matching is
///     case-insensitive, so a rename to <c>featuretests.cs</c> cannot escape the policy.
/// </remarks>
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
