namespace XE_Local_AI_Engine.Client.Services.Development;

using System.Security.Cryptography;
using System.Text.Json;
using XE_Local_AI_Engine.Client.Common;

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
/// <param name="ImportDigest">
///     SHA-256 of the imported <c>.xe-dev/profile.json</c> bytes, or null when none shipped. Part of the canonical
///     digest, so a changed declaration yields a new profile.
/// </param>
internal sealed record DevelopmentCommandProfile(
    string ProfileId,
    string ProfileVersion,
    string? TemplateId,
    string? BuildTarget,
    // Not the per-attempt tamper baseline: this hashes the operator's live tree at project creation, while the managed
    // worktree sits at the attempt's base commit, so that check captures its own baseline at attempt start.
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
        return Utf8JsonBuffer.Write(writer =>
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
        }, new JsonWriterOptions
        {
            Indented = false
        });
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

    private static readonly JsonSerializerOptions CanonicalReadOptions = JsonSerializerOptions.Web;
}
