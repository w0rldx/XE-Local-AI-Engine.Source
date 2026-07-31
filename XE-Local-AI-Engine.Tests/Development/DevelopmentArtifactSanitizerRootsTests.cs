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
    public void Sanitize_WithHostRootsOnly_LosesTheFileAndLineEntirely()
    {
        // The defect, reproduced. Container-internal paths never match a host root, so the targeted pass is a silent
        // no-op and the generic pattern swallows the whole diagnostic. Nothing errors — the evidence just stops saying
        // which file failed, which is why this needed a test rather than a code read.
        var sanitized = DevelopmentArtifactSanitizer.Sanitize(Evidence(ContainerBuildReport),
            RepositoryRoot,
            HostWorkspace,
            HostRuntime);

        AssertEx.False(sanitized.StandardOutput.Contains("Calculator.cs", StringComparison.Ordinal), sanitized.StandardOutput);
        AssertEx.False(sanitized.StandardOutput.Contains("(17,32)", StringComparison.Ordinal), sanitized.StandardOutput);
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
