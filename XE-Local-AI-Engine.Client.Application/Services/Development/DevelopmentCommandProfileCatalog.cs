namespace XE_Local_AI_Engine.Client.Services.Development;

using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;

/// <summary>
///     The code-owned command-profile catalog. It ships three .NET-family profiles and no user-defined ones:
///     a custom profile would let a repository describe its own build and test commands, which needs the container
///     isolation that is not built yet, and a <c>cargo</c> profile on a host without <c>cargo</c> would only upgrade
///     "missing solution" into "command not found".
/// </summary>
internal static class DevelopmentCommandProfileCatalog
{
    public const string DotnetSlnx = "dotnet-slnx";
    public const string DotnetCsproj = "dotnet-csproj";

    /// <summary>
    ///     The profile for a repository with no detected .NET build system. Its validation profile is the whitespace
    ///     check alone, which is honest rather than false-green: the gate reports exactly what it verified, and the
    ///     profile is surfaced to the operator at confirmation so "no build system was detected" is a visible decision
    ///     rather than a silent downgrade.
    /// </summary>
    public const string GenericGit = "generic-git";

    /// <summary>
    ///     Bumped whenever the command set, argument vectors, timeouts or protected paths of any profile below change
    ///     — and equally whenever <see cref="DependencyManifestPaths" /> gains or loses a rule, because that set is a
    ///     gate applied to every attempt run under a profile this catalog issued.
    ///     A stored profile whose <c>(ProfileId, ProfileVersion)</c> still resolves here but whose canonical content no
    ///     longer matches is rejected rather than silently re-interpreted — see
    ///     <see cref="ResolveStored" />.
    /// </summary>
    public const string CurrentVersion = "v2";

    /// <summary>
    ///     The files whose content decides what <c>restore</c> resolves. A change to any of them fails deterministic
    ///     validation with <see cref="DevelopmentValidationFailureCodes.DependencyManifestChanged" /> — see
    ///     <see cref="DevelopmentDependencyManifestPolicy" /> for why that is a verdict rather than a security
    ///     exception.
    ///     <para>
    ///         It lives HERE, beside <see cref="DefaultProtectedPaths" /> and under
    ///         <see cref="CurrentVersion" />'s rule, rather than in the policy class: the set is the whole of the
    ///         control, and a control versioned by a mechanism of its own would be a second thing to keep in step with
    ///         the profile a project was created under. It is deliberately NOT a field of
    ///         <see cref="DevelopmentCommandProfile" /> — it is code-owned and identical for every profile, so putting
    ///         it in the canonical digest would invalidate every stored profile to say nothing new.
    ///     </para>
    ///     <para>
    ///         <c>Directory.Build.props</c> and <c>Directory.Build.targets</c> are included on the operator's 2026-08-25
    ///         ruling. They are <em>build</em> configuration rather than dependency manifests, but either can carry a
    ///         <c>PackageReference</c>, and excluding them would leave exactly the bypass the rule exists to close.
    ///         Note this is a different shape from <c>EnsureBuildConfigurationBarrier</c>, which bounds MSBuild's
    ///         upward search to configuration from <em>above</em> the workspace.
    ///     </para>
    ///     <para>
    ///         The set is the control, so a packaging system missing from it is a hole rather than a gap in coverage.
    ///         Adding one is a source change here plus a <see cref="CurrentVersion" /> bump.
    ///     </para>
    /// </summary>
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
    ///     <para>
    ///         Grounded in the measured layout of this repository, <c>XE-Framework</c> and the synthetic fixture rather
    ///         than assumed. Three things this set deliberately encodes:
    ///     </para>
    ///     <para>
    ///         The filename rules carry most of the weight: <c>*Tests.cs</c> matched 543 files across both real
    ///         repositories with zero false positives. The directory rules exist only to close the shared-helper hole —
    ///         without them the agent could gut <c>AssertEx.cs</c> so every assertion silently passes, which is a
    ///         shorter path to green than deleting a test and is exactly the move the test-write policy exists to stop.
    ///     </para>
    ///     <para>
    ///         The directory rules are scoped to <c>*.cs</c> on purpose. Freezing whole test directories would also
    ///         freeze the nine test <c>.csproj</c> files, so the agent could never add a package reference to an
    ///         existing test project — which blocks the "implement a feature and its tests" case the test-write policy
    ///         explicitly permits.
    ///     </para>
    ///     <para>
    ///         <c>**&#47;*.Tests&#47;**</c> alone would be a trap and is not used alone: no directory in
    ///         <c>XE-Framework</c> ends in <c>.Tests</c> (its projects are <c>XeFramework.Tests.UnitTests</c> and
    ///         siblings), and it also misses this repository's own <c>XE-Local-AI-Engine.Tests.E2ETests</c>. Matching
    ///         only that pattern would protect zero tests on this very repository.
    ///     </para>
    ///     <para>
    ///         Not included, each for a measured reason: <c>*Spec.cs</c> has three false positives across the two
    ///         repositories (<c>OrchestrationSpec.cs</c>, <c>LlamaServerLaunchSpec.cs</c>, <c>ImageServerLaunchSpec.cs</c>)
    ///         and zero true positives; <c>*Test.cs</c> singular matches nothing in either repository.
    ///     </para>
    /// </summary>
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
    ///     Resolves a profile that was snapshotted into the database, and re-derives it from the code-owned catalog to
    ///     prove the definition has not changed underneath it.
    ///     <para>
    ///         This is the reuse rule. If someone edits a profile's commands without bumping
    ///         <see cref="CurrentVersion" />, every already-created project would silently start running different
    ///         commands under a version string that claims otherwise. Rejecting is the only safe answer: the stored
    ///         bytes are the operator-confirmed agreement, and the code no longer honours it.
    ///     </para>
    ///     <para>
    ///         This does <em>not</em> touch the three artifact protocol versions
    ///         (<c>development-workspace-v1</c>, <c>development-validation-v2</c>, <c>development-review-v1</c>) or the
    ///         gates that compare them. Those describe artifact shape compatibility; this describes command content.
    ///         They are separate dimensions.
    ///     </para>
    /// </summary>
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
