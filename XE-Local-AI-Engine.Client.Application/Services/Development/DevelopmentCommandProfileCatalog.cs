namespace XE_Local_AI_Engine.Client.Services.Development;

using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;

/// <summary>
///     The code-owned command-profile catalog, shipping three .NET-family profiles and no user-defined ones.
/// </summary>
/// <remarks>
///     A custom profile would let a repository describe its own build and test commands, which needs the container
///     isolation that is not built yet; and a <c>cargo</c> profile on a host without <c>cargo</c> would only upgrade
///     "missing solution" into "command not found".
/// </remarks>
internal static class DevelopmentCommandProfileCatalog
{
    public const string DotnetSlnx = "dotnet-slnx";
    public const string DotnetCsproj = "dotnet-csproj";

    /// <summary>The profile for a repository with no detected .NET build system.</summary>
    /// <remarks>
    ///     Its validation profile is the whitespace check alone, which is honest rather than false-green: the gate
    ///     reports exactly what it verified, and the profile is surfaced to the operator at confirmation, so "no
    ///     build system was detected" is a visible decision rather than a silent downgrade.
    /// </remarks>
    public const string GenericGit = "generic-git";

    /// <summary>
    ///     Bumped whenever the command set, argument vectors, timeouts or protected paths of any profile below change,
    ///     and equally whenever <see cref="DependencyManifestPaths" /> gains or loses a rule.
    /// </summary>
    /// <remarks>
    ///     That set is a gate applied to every attempt run under a profile this catalog issued. A stored profile whose
    ///     <c>(ProfileId, ProfileVersion)</c> still resolves here but whose canonical content no longer matches is
    ///     rejected rather than silently re-interpreted — see <see cref="ResolveStored" />.
    /// </remarks>
    public const string CurrentVersion = "v2";

    /// <summary>
    ///     The files whose content decides what <c>restore</c> resolves; changing one fails deterministic validation
    ///     with <see cref="DevelopmentValidationFailureCodes.DependencyManifestChanged" />, a verdict rather than a
    ///     security exception.
    /// </summary>
    /// <remarks>
    ///     The set is the whole of the control: a packaging system missing from it is a hole, not a gap in coverage,
    ///     and adding one is a source change here plus a <see cref="CurrentVersion" /> bump. It is not a field of
    ///     <see cref="DevelopmentCommandProfile" /> — code-owned and identical for every profile, so the canonical
    ///     digest would invalidate every stored profile to say nothing new. <c>Directory.Build.props</c> and
    ///     <c>.targets</c> are in because either can carry a <c>PackageReference</c>.
    /// </remarks>
    public static readonly string[] DependencyManifestPaths =
    [
        "**/*.csproj",
        "**/Directory.Packages.props",
        "**/Directory.Build.props",
        "**/Directory.Build.targets",
        "**/packages.lock.json",
        "**/global.json",
        "**/NuGet.config",
        "**/package.json",
        "**/package-lock.json",
        "**/pnpm-lock.yaml",
        "**/yarn.lock",
        "**/Cargo.toml",
        "**/Cargo.lock",
        "**/requirements*.txt",
        "**/pyproject.toml",
        "**/uv.lock",
        "**/poetry.lock"
    ];

    /// <summary>
    ///     Paths the agent may create but may not modify or delete once they existed at <c>BaseCommit</c> — the
    ///     test-write policy.
    /// </summary>
    /// <remarks>
    ///     The filename rules carry most of the weight — <c>*Tests.cs</c> matches 543 files across two real
    ///     repositories with zero false positives — while the directory rules exist only to close the shared-helper
    ///     hole and are scoped to <c>*.cs</c>, so an agent can still add a package reference to an existing test
    ///     project. What each pattern is grounded in, and what is deliberately left out, is in
    ///     <c>docs/wiki/12-security-and-privacy.md</c> ("The test-write policy's protected-path set").
    /// </remarks>
    public static readonly string[] DefaultProtectedPaths =
    [
        "**/*Tests.cs",
        "**/*.test.ts",
        "**/*.test.tsx",
        "**/*.Tests/**/*.cs",
        "**/*.UnitTests/**/*.cs",
        "**/*.IntegrationTests/**/*.cs",
        "**/*.E2ETests/**/*.cs",
        "tests/**/*.cs",
        "test/**/*.cs"
    ];

    /// <summary>
    ///     Builds the profile for a code-owned id. <paramref name="buildTarget" /> is the repository-relative solution or
    ///     project file the .NET commands operate on, and must be null for <see cref="GenericGit" />.
    /// </summary>
    public static DevelopmentCommandProfile Materialize(string profileId,
        string? buildTarget,
        string? templateId = null,
        string? importDigest = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        var normalizedTarget = NormalizeTarget(profileId, buildTarget);
        return profileId switch
        {
            GenericGit => new DevelopmentCommandProfile(GenericGit,
                CurrentVersion,
                templateId,
                BuildTarget: null,
                importDigest,
                [GitStatus(), GitDiffCheck()],
                [DevelopmentCommandIds.GitDiffCheck],
                DefaultProtectedPaths,
                IsCustom: false).Validated(),
            DotnetSlnx or DotnetCsproj => new DevelopmentCommandProfile(profileId,
                CurrentVersion,
                templateId,
                normalizedTarget,
                importDigest,
                [
                    GitStatus(),
                    GitDiffCheck(),
                    new DevelopmentProfileCommand(DevelopmentCommandIds.DotnetRestore,
                        "dotnet",
                        ["restore", normalizedTarget!],
                        RestoreTimeoutSeconds),
                    new DevelopmentProfileCommand(DevelopmentCommandIds.DotnetBuildRelease,
                        "dotnet",
                        ["build", normalizedTarget!, "--configuration", "Release", "--no-restore"],
                        BuildTimeoutSeconds),
                    new DevelopmentProfileCommand(DevelopmentCommandIds.DotnetTestRelease,
                        "dotnet",
                        ["test", normalizedTarget!, "--configuration", "Release", "--no-build", "--max-parallel-test-modules", "1"],
                        TestTimeoutSeconds)
                ],
                [
                    DevelopmentCommandIds.GitDiffCheck,
                    DevelopmentCommandIds.DotnetRestore,
                    DevelopmentCommandIds.DotnetBuildRelease,
                    DevelopmentCommandIds.DotnetTestRelease
                ],
                DefaultProtectedPaths,
                IsCustom: false).Validated(),
            _ => throw new DevelopmentWorkspaceSecurityException("The requested Development command profile is not in the code-owned catalog.")
        };
    }

    /// <summary>
    ///     Resolves a profile snapshotted into the database, re-deriving it from the code-owned catalog to prove the
    ///     definition has not changed underneath it.
    /// </summary>
    /// <remarks>
    ///     Editing a profile's commands without bumping <see cref="CurrentVersion" /> would silently start every
    ///     already-created project on different commands under a version string claiming otherwise, and the stored
    ///     bytes are the operator-confirmed agreement, so rejecting is the only safe answer. This does not touch the
    ///     three artifact protocol versions (<c>development-workspace-v1</c>, <c>development-validation-v2</c>,
    ///     <c>development-review-v1</c>) or their gates: those describe shape compatibility, this command content.
    /// </remarks>
    public static DevelopmentCommandProfile ResolveStored(string? storedProfileJson)
    {
        if (string.IsNullOrWhiteSpace(storedProfileJson))
        {
            throw new DevelopmentWorkspaceSecurityException("The Development project has no command profile. Re-register the repository to detect and confirm one.");
        }

        var stored = DevelopmentCommandProfile.FromCanonicalJson(storedProfileJson);
        if (stored.IsCustom)
        {
            throw new DevelopmentWorkspaceSecurityException("Custom Development command profiles are not supported yet; they require the container sandbox.");
        }

        var expected = Materialize(stored.ProfileId, stored.BuildTarget, stored.TemplateId, stored.ImportDigest);
        if (!string.Equals(expected.ProfileVersion, stored.ProfileVersion, StringComparison.Ordinal))
        {
            throw new DevelopmentWorkspaceSecurityException("The stored Development command profile was produced by a different catalog version.");
        }

        if (!expected.ToCanonicalUtf8().AsSpan().SequenceEqual(stored.ToCanonicalUtf8()))
        {
            throw new DevelopmentWorkspaceSecurityException("The code-owned Development command profile changed without a version bump, so the stored profile can no longer be trusted.");
        }

        return stored;
    }

    private const int GitTimeoutSeconds = 120;
    private const int RestoreTimeoutSeconds = 900;
    private const int BuildTimeoutSeconds = 1800;
    private const int TestTimeoutSeconds = 1800;

    private static DevelopmentProfileCommand GitStatus() =>
        new(DevelopmentCommandIds.GitStatus,
            AgentHomeGit.Executable,
            AgentHomeGit.Arguments("status", "--short", "--branch", "--untracked-files=all", "--", "."),
            GitTimeoutSeconds);

    private static DevelopmentProfileCommand GitDiffCheck() =>
        new(DevelopmentCommandIds.GitDiffCheck,
            AgentHomeGit.Executable,
            AgentHomeGit.Arguments("diff", "--check", "HEAD", "--", "."),
            GitTimeoutSeconds);

    /// <summary>
    ///     The build target is a repository-relative path that becomes a literal process argument, so it is confined by
    ///     the same rules as any agent-supplied workspace path before it can reach an argument vector.
    /// </summary>
    private static string? NormalizeTarget(string profileId, string? buildTarget)
    {
        if (string.Equals(profileId, GenericGit, StringComparison.Ordinal))
        {
            return buildTarget is null
                ? null
                : throw new DevelopmentWorkspaceSecurityException("The generic Development command profile does not take a build target.");
        }

        if (string.IsNullOrWhiteSpace(buildTarget))
        {
            throw new DevelopmentWorkspaceSecurityException("A .NET Development command profile requires a solution or project build target.");
        }

        var confined = DevelopmentWorkspaceSecurity.Confine(buildTarget, allowRoot: false);
        if (!confined.IsAccepted)
        {
            throw new DevelopmentWorkspaceSecurityException(confined.RejectionReason
                                                            ?? "The Development build target path was rejected.");
        }

        var expectedExtension = string.Equals(profileId, DotnetSlnx, StringComparison.Ordinal)
            ? new[]
            {
                ".slnx",
                ".sln"
            }
            : [".csproj"];
        if (!expectedExtension.Any(extension => confined.RelativePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
        {
            throw new DevelopmentWorkspaceSecurityException("The Development build target does not match the selected command profile.");
        }

        return confined.RelativePath;
    }
}
