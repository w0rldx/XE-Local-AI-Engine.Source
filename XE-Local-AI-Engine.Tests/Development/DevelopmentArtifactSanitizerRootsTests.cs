namespace XE_Local_AI_Engine.Tests.Development;

using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Artifact sanitisation when the command that produced the output ran somewhere other than the host.
///     <para>
///         The criterion these assert is a REACHABILITY one, taken literally: read a real failing-build report and
///         confirm the file and the line are still legible. Asserting that redaction "ran" would pass against output in
///         which every path — engine root and source location alike — had collapsed into one undifferentiated marker,
///         which is exactly the degraded state this exists to prevent.
///     </para>
/// </summary>
public sealed class DevelopmentArtifactSanitizerRootsTests
{
    /// <summary>
    ///     A real <c>dotnet build</c> failure, in the shape the runner emits it: the diagnostic names an absolute path,
    ///     and the project the diagnostic came from is repeated in brackets at the end of the line.
    /// </summary>
    private const string ContainerBuildReport = """
                                                  Determining projects to restore...
                                                  Restored /workspace/src/Lib/Lib.csproj (in 214 ms).
                                                /workspace/src/Lib/Calculator.cs(17,32): error CS1002: ; expected [/workspace/src/Lib/Lib.csproj]
                                                  restore packages to /xe-runtime/nuget
                                                Build FAILED.
                                                """;

    private const string HostWorkspace = "/home/dev/.local/share/xe/development/workspaces/aaaa/bbbb";
    private const string HostRuntime = "/home/dev/.local/share/xe/development/runtime/aaaa/bbbb";
    private const string RepositoryRoot = "/home/dev/projects/Calculator";

    [Test]
    public void Sanitize_WithContainerRoots_KeepsTheFileAndLineAReviewerNeeds()
    {
        var sanitized = DevelopmentArtifactSanitizer.Sanitize(Evidence(ContainerBuildReport),
            DevelopmentArtifactSanitizer.ResolveProtectedRoots(RepositoryRoot, Session(Mapped())));

        // The actual text, not "something was redacted".
        AssertEx.Contains(sanitized.StandardOutput, "/src/Lib/Calculator.cs(17,32): error CS1002: ; expected");
        AssertEx.Contains(sanitized.StandardOutput, "[REDACTED:development-path]/src/Lib/Calculator.cs");
        AssertEx.Contains(sanitized.StandardOutput, "Build FAILED.");

        // And the engine roots themselves are gone: nothing names the container's workspace or runtime mounts.
        AssertEx.False(sanitized.StandardOutput.Contains("/workspace/", StringComparison.Ordinal), sanitized.StandardOutput);
        AssertEx.False(sanitized.StandardOutput.Contains("/xe-runtime", StringComparison.Ordinal), sanitized.StandardOutput);
    }

    [Test]
    public void Sanitize_WithHostRootsOnly_KeepsTheLeafButLosesTheRepositoryRelativePrefix()
    {
        // Container-internal paths never match a host root, so the targeted pass is a silent no-op here and the
        // GENERIC pattern handles the diagnostic instead. That used to swallow it whole; it now keeps the trailing
        // segments, so the file and line survive even on the degraded path — this is the remedy, and asserting
        // it here is what stops the two passes from being confused for one another.
        var sanitized = DevelopmentArtifactSanitizer.Sanitize(Evidence(ContainerBuildReport),
            RepositoryRoot,
            HostWorkspace,
            HostRuntime);

        AssertEx.Contains(sanitized.StandardOutput, "Calculator.cs(17,32): error CS1002: ; expected");

        // The targeted pass is still the one worth having: it preserves the whole repository-relative path, while the
        // generic fallback keeps only a two-segment tail. "/src/Lib/Calculator.cs" survives one and not the other.
        AssertEx.False(sanitized.StandardOutput.Contains("/src/Lib/Calculator.cs", StringComparison.Ordinal), sanitized.StandardOutput);
        AssertEx.False(sanitized.StandardOutput.Contains("/workspace", StringComparison.Ordinal), sanitized.StandardOutput);
    }

    /// <summary>
    ///     As measured live: every <c>dotnet</c> command in the container gate died on a read-only <c>/tmp</c>,
    ///     and the stored report named no directory at all, so the one token that identified the fault was the one
    ///     token the sanitizer had removed.
    /// </summary>
    [Test]
    public void Sanitize_OnAReadOnlyFilesystemFailure_KeepsTheDirectoryThatIdentifiesTheFault()
    {
        const string Erofs = """
                             System.IO.IOException: The system cannot open the device or file specified. : 'NuGet-Migrations'.
                             One or more system calls failed: mkdir("/tmp/.dotnet/shm/session1", AllUsers_ReadWriteExecute) == -1; errno == EROFS;
                             """;

        var sanitized = DevelopmentArtifactSanitizer.Sanitize(Evidence(Erofs),
            DevelopmentArtifactSanitizer.ResolveProtectedRoots(RepositoryRoot, Session(Mapped())));

        // Actionable: which directory, and on which filesystem semantics it failed.
        AssertEx.Contains(sanitized.StandardOutput, "shm/session1");
        AssertEx.Contains(sanitized.StandardOutput, "errno == EROFS");

        // And the leading segments — the only part that can carry host layout — are still gone.
        AssertEx.False(sanitized.StandardOutput.Contains("/tmp/", StringComparison.Ordinal), sanitized.StandardOutput);
        AssertEx.Contains(sanitized.StandardOutput, "[REDACTED:development-path]");
    }

    /// <summary>
    ///     The conservative half of the same change: a tail is kept only where there is a tail LEFT once the leading
    ///     two named segments are destroyed. Anything shallower — which is exactly the shape a home directory, a
    ///     mount point or a UNC share root has — is still redacted whole.
    /// </summary>
    [Test]
    public void Sanitize_KeepsNoTailForPathsShallowEnoughToNameTheHostOrItsUser()
    {
        var sanitized = DevelopmentArtifactSanitizer.SanitizeText("could not read /home/dev or /etc/passwd or C:\\Users\\operator\\report.txt or \\\\fileserver\\share\\notes.txt",
            RepositoryRoot);

        AssertEx.False(sanitized.Contains("/home/dev", StringComparison.Ordinal), sanitized);
        AssertEx.False(sanitized.Contains("passwd", StringComparison.Ordinal), sanitized);

        // The Windows drive letter must not count as a segment, or "operator" — the user name — lands in the tail.
        AssertEx.False(sanitized.Contains("operator", StringComparison.Ordinal), sanitized);
        AssertEx.Contains(sanitized, "report.txt");

        // A UNC share: the server and the share name are the identity, the leaf is not.
        AssertEx.False(sanitized.Contains("fileserver", StringComparison.Ordinal), sanitized);
        AssertEx.False(sanitized.Contains("share", StringComparison.Ordinal), sanitized);
        AssertEx.Contains(sanitized, "notes.txt");
    }

    /// <summary>
    ///     A principal is named by whatever follows a home-directory CONTAINER, and that is not always the second
    ///     segment — so a purely positional "destroy the first two" rule leaks at other depths. Both of these were
    ///     verified to leak before the container anchor was added, and both are reachable on the box this engine is
    ///     developed on: WSL2 mounts the Windows profile at <c>/mnt/c/Users/&lt;user&gt;</c> (principal FOURTH), and the
    ///     rootless Docker socket lives under <c>/run/user/&lt;uid&gt;</c> (principal THIRD).
    /// </summary>
    [Test]
    public void Sanitize_RedactsThePrincipalAtAnyDepth_NotJustTheSecondSegment()
    {
        var sanitized = DevelopmentArtifactSanitizer.SanitizeText("denied /mnt/c/Users/operator and /mnt/c/Users/operator/projects and /run/user/1000/docker.sock",
            RepositoryRoot);

        // The Windows account name is the identity here, and it sits FOURTH.
        AssertEx.False(sanitized.Contains("operator", StringComparison.Ordinal), sanitized);

        // The uid is the identity here, and it sits THIRD.
        AssertEx.False(sanitized.Contains("1000", StringComparison.Ordinal), sanitized);

        // Still diagnosable: the leaf that says WHAT was touched survives.
        AssertEx.Contains(sanitized, "docker.sock");
        AssertEx.Contains(sanitized, "projects");
    }

    [Test]
    public void Sanitize_OnHostOutputUnderTheProcessProvider_KeepsTheFileAndLineToo()
    {
        // The same criterion on the currently shipping path. It did NOT hold before this change: the targeted pass
        // replaced the engine root and the generic pattern then ate the remainder, because the marker's closing bracket
        // is not in its excluded lookbehind class. Both providers now produce a legible diagnostic.
        var hostReport = ContainerBuildReport
                         .Replace("/workspace", HostWorkspace, StringComparison.Ordinal)
                         .Replace("/xe-runtime", HostRuntime, StringComparison.Ordinal);

        var sanitized = DevelopmentArtifactSanitizer.Sanitize(Evidence(hostReport),
            DevelopmentArtifactSanitizer.ResolveProtectedRoots(RepositoryRoot, Session(Identity())));

        AssertEx.Contains(sanitized.StandardOutput, "/src/Lib/Calculator.cs(17,32): error CS1002: ; expected");
        AssertEx.False(sanitized.StandardOutput.Contains("/home/dev", StringComparison.Ordinal), sanitized.StandardOutput);
    }

    [Test]
    public void Sanitize_StillRedactsAnAbsolutePathThatIsNotUnderAnyEngineRoot()
    {
        // The relaxation must not become a hole: only the remainder of an ALREADY-redacted path survives. An unrelated
        // absolute path is redacted exactly as before.
        var sanitized = DevelopmentArtifactSanitizer.Sanitize(Evidence("could not open /etc/shadow while building"),
            DevelopmentArtifactSanitizer.ResolveProtectedRoots(RepositoryRoot, Session(Mapped())));

        AssertEx.False(sanitized.StandardOutput.Contains("/etc/shadow", StringComparison.Ordinal), sanitized.StandardOutput);
        AssertEx.Contains(sanitized.StandardOutput, "[REDACTED:development-path]");
    }

    /// <summary>
    ///     FU4-1: a prompt is ENGINE-authored text, so it redacts rather than rejecting. Both inputs here are shapes a
    ///     real coder prompt carries — the workspace's carried-file list names paths, and a rework brief quotes the
    ///     gate's own complaint, which quotes a failing test name — and each on its own clears the secret scanner's
    ///     keyword-free entropy fallback. Rejecting on a match would lose the prompt of exactly the attempts whose
    ///     prompt is worth having.
    /// </summary>
    [Test]
    public void SanitizePromptText_RedactsAProtectedRootAndAHighEntropyToken_InsteadOfRejecting()
    {
        const string Prompt = "Task: fix the build\nFiles in this shared workspace that already differ from the base commit: "
                              + RepositoryRoot
                              + "/src/Lib/Calculator.cs\nfailing test ApplyThinkingSwitch_MarkerAbsent_BodyHasNoChatTemplateKwargs\n";

        var sanitized = DevelopmentArtifactSanitizer.SanitizePromptText(Prompt, RepositoryRoot);

        AssertEx.False(sanitized.Contains(RepositoryRoot, StringComparison.Ordinal), sanitized);
        AssertEx.Contains(sanitized, "[REDACTED:development-path]/src/Lib/Calculator.cs");
        AssertEx.Contains(sanitized, "[REDACTED:high-entropy-token]");

        // Still a usable prompt record: the instruction around the redactions survives.
        AssertEx.Contains(sanitized, "Task: fix the build");
        AssertEx.Contains(sanitized, "failing test ");

        // The distinction being asserted: the model-authored path rejects the very same text outright.
        Assert.Throws<DevelopmentWorkspaceSecurityException>(() => DevelopmentArtifactSanitizer.SanitizeText(Prompt, RepositoryRoot));
    }

    /// <summary>
    ///     The 2026-09-05 live round read every coder and reviewer prompt artifact and found exactly one redaction in
    ///     each: the whole "Protected test patterns" line. The generic Unix pattern was firing on the '/' inside
    ///     <c>**/*Tests.cs</c> — glob syntax, preceded by '*' rather than by a host name — so all nine of
    ///     <see cref="DevelopmentCommandProfileCatalog.DefaultProtectedPaths" /> collapsed into markers, and the one
    ///     line of the prompt that says which files the coder may not touch was the one line an operator could not read.
    ///     <para>
    ///         The line is taken from <see cref="DevelopmentTestWritePolicy.Prompt" /> rather than transcribed, so a
    ///         future pattern added to the catalog is covered here the day it ships.
    ///     </para>
    /// </summary>
    [Test]
    public void SanitizePromptText_KeepsTheProtectedTestPatternGlobs_AndStillRedactsARealAbsolutePath()
    {
        var profile = DevelopmentCommandProfileCatalog.Materialize(DevelopmentCommandProfileCatalog.DotnetSlnx, "Calc.slnx");
        var prompt = DevelopmentTestWritePolicy.Prompt(profile)
                     + "\nFiles in this shared workspace that already differ from the base commit: "
                     + RepositoryRoot
                     + "/src/Lib/Calculator.cs\ncould not open /etc/shadow, nor \"/etc/shadow\", nor XE_HOME=/etc/shadow, nor (/etc/shadow), nor */etc/shadow\n";

        var sanitized = DevelopmentArtifactSanitizer.SanitizePromptText(prompt, RepositoryRoot);

        // The whole rendered line, separators and all — the actual text, not "nothing was redacted". Asserting the
        // nine globs one at a time would pass against a line whose commas and spaces had been eaten around them.
        AssertEx.Contains(sanitized, DevelopmentTestWritePolicy.Prompt(profile));

        // The relaxation is not a hole. A path under a protected root still loses the root, and an unrelated absolute
        // path is still redacted at every delimiter a prompt puts in front of one — including a single literal '*',
        // which is not the glob token and does not buy an exemption.
        AssertEx.False(sanitized.Contains(RepositoryRoot, StringComparison.Ordinal), sanitized);
        AssertEx.Contains(sanitized, "[REDACTED:development-path]/src/Lib/Calculator.cs");
        AssertEx.False(sanitized.Contains("/etc/shadow", StringComparison.Ordinal), sanitized);
    }

    /// <summary>
    ///     The exemption above is the <c>**</c> glob token and nothing wider. A single <c>*</c> glued to a real
    ///     absolute path is the shape that would turn a legibility fix into a leak, because <c>SanitizeText</c> is the
    ///     boundary <see cref="DevelopmentCloudContextBuilder" /> crosses when it sends requirements and file excerpts
    ///     to a cloud provider — so <c>*/etc/shadow</c>, and a C comment closer butted against a path inside an
    ///     excerpt, must still redact.
    ///     <para>
    ///         The second half asserts the residual that remains rather than hiding it: a real absolute path written
    ///         immediately after <c>**</c> survives, because that prefix is indistinguishable from a glob at this
    ///         layer. It is accepted for the same reason the <c>]</c> exclusion's residual is — the engine's own roots
    ///         are replaced by literal match in the pass BEFORE these patterns, whatever precedes them, so what
    ///         survives is an unrelated path a model volunteered. Asserting it makes any future widening a decision.
    ///     </para>
    /// </summary>
    [Test]
    public void SanitizePromptText_ExemptsTheDoubleStarGlobTokenOnly_AndPinsTheResidualPathAfterIt()
    {
        var single = DevelopmentArtifactSanitizer.SanitizePromptText("could not open */etc/shadow", RepositoryRoot);

        AssertEx.False(single.Contains("/etc/shadow", StringComparison.Ordinal), single);
        AssertEx.Contains(single, "[REDACTED:development-path]");

        var comment = DevelopmentArtifactSanitizer.SanitizePromptText("close the block */etc/shadow is still a host path", RepositoryRoot);

        AssertEx.False(comment.Contains("/etc/shadow", StringComparison.Ordinal), comment);

        // Documented residual, pinned deliberately: '**' exempts whatever follows it, so this path is NOT redacted.
        var residual = DevelopmentArtifactSanitizer.SanitizePromptText("excluded: **/home/user/repo/x.cs", RepositoryRoot);

        AssertEx.Contains(residual, "**/home/user/repo/x.cs");
    }

    [Test]
    public void ResolveProtectedRoots_CoversBothTheHostAndTheSandboxNamesForTheSameDirectories()
    {
        var roots = DevelopmentArtifactSanitizer.ResolveProtectedRoots(RepositoryRoot, Session(Mapped()));

        AssertEx.Contains(roots, RepositoryRoot);
        AssertEx.Contains(roots, HostWorkspace);
        AssertEx.Contains(roots, HostRuntime);
        AssertEx.Contains(roots, "/workspace");
        AssertEx.Contains(roots, "/xe-runtime/nuget");
        // The runtime mount ROOT as well as its leaves: a build prints the parent too, and leaving it to the generic
        // pattern would swallow the relative remainder that follows it.
        AssertEx.Contains(roots, "/xe-runtime");
    }

    private static DevelopmentCommandEvidence Evidence(string standardOutput) =>
        new(DevelopmentCommandIds.DotnetBuildRelease,
            ExitCode: 1,
            Completed: true,
            OutputTruncated: false,
            DurationMilliseconds: 1234,
            standardOutput,
            StandardError: string.Empty);

    private static IReadOnlyList<SandboxMountBinding> Mapped() =>
    [
        new(HostWorkspace, "/workspace", ReadOnly: false),
        new(HostRuntime + "/home", "/xe-runtime/home", ReadOnly: false),
        new(HostRuntime + "/tmp", "/xe-runtime/tmp", ReadOnly: false),
        new(HostRuntime + "/nuget", "/xe-runtime/nuget", ReadOnly: false),
        new(HostRuntime + "/dotnet", "/xe-runtime/dotnet", ReadOnly: false)
    ];

    private static IReadOnlyList<SandboxMountBinding> Identity() =>
    [
        new(HostWorkspace, HostWorkspace, ReadOnly: false),
        new(HostRuntime + "/home", HostRuntime + "/home", ReadOnly: false),
        new(HostRuntime + "/nuget", HostRuntime + "/nuget", ReadOnly: false)
    ];

    private static DevelopmentWorkspaceSession Session(IReadOnlyList<SandboxMountBinding> mounts) =>
        new(Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "0000000000000000000000000000000000000000",
            "identity",
            HostWorkspace,
            HostRuntime,
            new SandboxHandle
            {
                ProviderName = "test",
                SandboxId = "sandbox",
                AttachKey = new SandboxAttachKey
                {
                    OwnerUserId = "owner",
                    NodeId = "node",
                    ProviderName = "test",
                    RuntimeProfile = "development",
                    ManifestVersion = 2
                },
                CreatedAt = DateTimeOffset.UnixEpoch,
                ManifestVersion = 2,
                Mounts = mounts
            });
}
