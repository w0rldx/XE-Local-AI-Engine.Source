namespace XE_Local_AI_Engine.Tests.Sandbox;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     What the process jail does and does NOT confine for a command the MODEL chose, under the isolation mode
///     AgentHome actually uses (<see cref="SandboxIsolationMode.None" /> — see <c>SandboxWorkloads.AgentHome</c>'s
///     <c>IsolationFloor</c>).
///     <para>
///         This class exists because every other confinement test in this directory grades the PROVIDER's own file
///         operations — <c>CopyIntoAsync</c>, <c>ReadFileAsync</c>, <c>CopyOutAsync</c>, working-directory
///         resolution — which really are jailed. None of them grades the child process's own syscalls, and
///         <c>SandboxIsolationLiveTests.IsolatedCommand_CannotSeeTheHostFilesystem_ButCanWriteItsOwnJail</c> proves
///         the filesystem boundary only for <see cref="SandboxIsolationMode.Filesystem" />, which AgentHome does not
///         request. The gap between those two facts is exactly what an operator reading "unrestricted but JAILED"
///         would get wrong.
///     </para>
///     <para>
///         <b>These tests assert the real behaviour, including where it is weaker than the name suggests.</b> A test
///         that documents a limit is worth more than no test: if the product later gains a filesystem boundary for
///         this mode, this class goes red and whoever changed it has to come and update the claim rather than leave
///         a stale one standing. Nothing here is destructive — each probe writes one marker file into a temporary
///         directory the test owns and deletes afterwards, and reads one file it created itself.
///     </para>
/// </summary>
[Category(TestCategories.Integration)]
public sealed class ProcessSandboxFilesystemReachTests : IDisposable
{
    private readonly List<string> _tempPaths = [];

    public void Dispose()
    {
        foreach (var path in _tempPaths)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
                else if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
                // Best-effort temp cleanup.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort temp cleanup.
            }
        }
    }

    /// <summary>
    ///     The headline fact: under the mode AgentHome uses, a model-chosen command WRITES OUTSIDE THE JAIL. The
    ///     working directory is set to the workspace copy and nothing else constrains the child's filesystem view, so
    ///     an absolute path in the command's own arguments reaches anywhere the engine's own user can reach —
    ///     including the operator's ORIGINAL registered folder, which the workspace copy exists to protect.
    /// </summary>
    [Test]
    public async Task ModelChosenCommand_UnderTheModeAgentHomeUses_CanWriteAnAbsolutePathOutsideTheJail()
    {
        SkipUnlessPosixJail();

        var outside = CreateTempDirectory("xe-sbx-outside");
        var marker = Path.Combine(outside, "written-by-the-sandbox.txt");

        using var provider = CreateProvider();
        var handle = await CreateAgentHomeSandboxAsync(provider);

        var result = await provider.ExecuteAsync(handle,
            new SandboxCommandRequest
            {
                ExecutionId = "reach-write",
                Executable = "/bin/sh",
                Arguments = ["-c", $"printf 'escaped\\n' > '{marker}'"],
                WorkingDirectory = AgentHomeGit.WorkspaceSelectedRoot,
                Timeout = TimeSpan.FromSeconds(30)
            });

        AssertEx.True(result.Completed, $"the probe command must run: {result.StandardError}");
        AssertEx.Equal(expected: 0, result.ExitCode, result.StandardError);

        // DOCUMENTED LIMIT, not an aspiration: this assertion passing is the finding. "The original host folders are
        // never modified" is a CONVENTION of what the node's own code does, not a control the jail enforces — a model
        // granted run_commands breaks it with one absolute path.
        AssertEx.True(File.Exists(marker),
            "the process jail does not confine a child's filesystem writes under SandboxIsolationMode.None. "
            + "If this went RED, the product gained a boundary here and every claim about run_command's reach "
            + "(docs/wiki/12-security-and-privacy.md §7, AgentHomeGitHardening's remarks, the S12B report) must be revisited.");
        AssertEx.Equal("escaped\n", await File.ReadAllTextAsync(marker));
    }

    /// <summary>
    ///     The same fact for reads, which is what makes the environment scrub a partial control: the child cannot
    ///     inherit the worker's secrets through its ENVIRONMENT, but it can read any file the engine's user can open.
    ///     The probe reads a file the test planted, never a real credential.
    /// </summary>
    [Test]
    public async Task ModelChosenCommand_UnderTheModeAgentHomeUses_CanReadAnAbsolutePathOutsideTheJail()
    {
        SkipUnlessPosixJail();

        var outside = CreateTempDirectory("xe-sbx-outside-read");
        var secretish = Path.Combine(outside, "pretend-credential.txt");
        await File.WriteAllTextAsync(secretish, "sentinel-value\n");

        using var provider = CreateProvider();
        var handle = await CreateAgentHomeSandboxAsync(provider);

        var result = await provider.ExecuteAsync(handle,
            new SandboxCommandRequest
            {
                ExecutionId = "reach-read",
                Executable = "/bin/cat",
                Arguments = [secretish],
                WorkingDirectory = AgentHomeGit.WorkspaceSelectedRoot,
                Timeout = TimeSpan.FromSeconds(30)
            });

        AssertEx.True(result.Completed, $"the probe command must run: {result.StandardError}");
        AssertEx.Contains(result.StandardOutput, "sentinel-value", StringComparison.Ordinal,
            "the process jail does not confine a child's filesystem reads under SandboxIsolationMode.None. "
            + "A file outside the workspace is readable by any command the model names.");
    }

    /// <summary>
    ///     The contrast, so the two modes cannot be confused: the SAME provider DOES confine the child when the
    ///     sandbox is created with <see cref="SandboxIsolationMode.Filesystem" />. AgentHome does not ask for it —
    ///     that is the decision, not a limitation of the backend — and this test is what makes the difference a fact
    ///     rather than a reading of the docs. It skips visibly where the host cannot deliver the mechanism.
    /// </summary>
    [Test]
    public async Task TheSameProvider_UnderFilesystemIsolation_CannotWriteOutsideTheJail()
    {
        SkipUnlessPosixJail();

        using var provider = CreateProvider();
        if (!provider.Capabilities.HasFlag(SandboxProviderCapabilities.SupportsFilesystemIsolation))
        {
            Skip.Test("BLOCKED: this host cannot deliver SandboxIsolationMode.Filesystem, so the contrast case cannot be measured here.");
        }

        var outside = CreateTempDirectory("xe-sbx-isolated");
        var marker = Path.Combine(outside, "must-not-exist.txt");

        var handle = await provider.CreateOrAttachAsync(new SandboxCreateRequest
        {
            AttachKey = AttachKey("owner-reach-iso", "node-reach-iso"),
            RuntimeProfile = "dotnet-agent-home",
            Isolation = SandboxIsolationMode.Filesystem,
            NetworkPolicy = SandboxNetworkPolicy.None
        });

        var result = await provider.ExecuteAsync(handle,
            new SandboxCommandRequest
            {
                ExecutionId = "reach-isolated",
                Executable = "/bin/sh",
                Arguments = ["-c", $"printf 'escaped\\n' > '{marker}' 2>/dev/null; echo done"],
                Timeout = TimeSpan.FromSeconds(30)
            });

        AssertEx.True(result.Completed, $"the probe command must run: {result.StandardError}");
        AssertEx.False(File.Exists(marker),
            "under SandboxIsolationMode.Filesystem the host path is not in the child's mount namespace at all, "
            + "so the write cannot land — this is the boundary AgentHome does NOT currently request");
    }

    /// <summary>
    ///     Reads under a boundary, the counterpart to the write case: a file planted outside the jail — standing in
    ///     for the operator's original registered folder, or for a credential — is not in the child's mount namespace
    ///     at all, so it cannot be opened. This is the whole reason `run_command` now ships only under isolation.
    /// </summary>
    [Test]
    public async Task TheSameProvider_UnderFilesystemIsolation_CannotReadOutsideTheJail()
    {
        SkipUnlessPosixJail();

        using var provider = CreateProvider();
        if (!provider.Capabilities.HasFlag(SandboxProviderCapabilities.SupportsFilesystemIsolation))
        {
            Skip.Test("BLOCKED: this host cannot deliver SandboxIsolationMode.Filesystem, so the contrast case cannot be measured here.");
        }

        var outside = CreateTempDirectory("xe-sbx-iso-read");
        var planted = Path.Combine(outside, "pretend-credential.txt");
        await File.WriteAllTextAsync(planted, "sentinel-value\n");

        var handle = await provider.CreateOrAttachAsync(new SandboxCreateRequest
        {
            AttachKey = AttachKey("owner-reach-isoread", "node-reach-isoread"),
            RuntimeProfile = "dotnet-agent-home",
            Isolation = SandboxIsolationMode.Filesystem,
            NetworkPolicy = SandboxNetworkPolicy.None
        });

        var result = await provider.ExecuteAsync(handle,
            new SandboxCommandRequest
            {
                ExecutionId = "reach-iso-read",
                Executable = "/bin/sh",
                Arguments = ["-c", $"cat '{planted}' 2>/dev/null; echo rc=$?"],
                Timeout = TimeSpan.FromSeconds(30)
            });

        AssertEx.True(result.Completed, $"the probe command must run: {result.StandardError}");
        AssertEx.False(result.StandardOutput.Contains("sentinel-value", StringComparison.Ordinal),
            "a path outside the jail is not in the isolated child's mount namespace, so its contents cannot be read");
        AssertEx.Contains(result.StandardOutput, "rc=1", StringComparison.Ordinal, "the read must fail, not return empty");
    }

    /// <summary>
    ///     <c>HOME</c> under isolation is a directory INSIDE the jail, not the operator's. That is what keeps a
    ///     command — and the node's own git, which reads <c>$HOME/.gitconfig</c> — away from the user's dotfiles
    ///     without relying on an environment allow-list to enumerate them.
    /// </summary>
    [Test]
    public async Task UnderFilesystemIsolation_HomeIsInsideTheJail_NotTheOperators()
    {
        SkipUnlessPosixJail();

        using var provider = CreateProvider();
        if (!provider.Capabilities.HasFlag(SandboxProviderCapabilities.SupportsFilesystemIsolation))
        {
            Skip.Test("BLOCKED: this host cannot deliver SandboxIsolationMode.Filesystem, so HOME under isolation cannot be measured here.");
        }

        var handle = await provider.CreateOrAttachAsync(new SandboxCreateRequest
        {
            AttachKey = AttachKey("owner-reach-isohome", "node-reach-isohome"),
            RuntimeProfile = "dotnet-agent-home",
            Isolation = SandboxIsolationMode.Filesystem,
            NetworkPolicy = SandboxNetworkPolicy.None
        });

        var result = await provider.ExecuteAsync(handle,
            new SandboxCommandRequest
            {
                ExecutionId = "reach-iso-home",
                Executable = "/bin/sh",
                Arguments = ["-c", "printf '[%s]\\n' \"$HOME\""],
                Timeout = TimeSpan.FromSeconds(30)
            });

        AssertEx.True(result.Completed, $"the probe command must run: {result.StandardError}");
        AssertEx.Contains(result.StandardOutput, SandboxIsolatedPaths.Home, StringComparison.Ordinal,
            "HOME under isolation is a jail subdirectory, so nothing reads the operator's dotfiles");
    }

    /// <summary>
    ///     What the jail DOES confine, so the finding is not read as "the sandbox does nothing": the worker's own
    ///     secret-bearing environment is not inherited. Pinned in full by
    ///     <c>ProcessSandboxRuntimeProviderTests.ProcessSandboxProvider_Execute_DoesNotLeakWorkerEnvironment…</c>;
    ///     this is the one-line restatement beside the reach tests above so the pair is read together.
    /// </summary>
    [Test]
    public async Task ModelChosenCommand_DoesNotInheritTheWorkersSecretBearingEnvironment()
    {
        SkipUnlessPosixJail();

        const string SecretName = "XE_REACH_TEST_PRETEND_SECRET";
        var previous = Environment.GetEnvironmentVariable(SecretName);
        try
        {
            Environment.SetEnvironmentVariable(SecretName, "sentinel-secret");

            using var provider = CreateProvider();
            var handle = await CreateAgentHomeSandboxAsync(provider);

            var result = await provider.ExecuteAsync(handle,
                new SandboxCommandRequest
                {
                    ExecutionId = "reach-env",
                    Executable = "/bin/sh",
                    Arguments = ["-c", $"printf '[%s]\\n' \"${SecretName}\""],
                    WorkingDirectory = AgentHomeGit.WorkspaceSelectedRoot,
                    Timeout = TimeSpan.FromSeconds(30)
                });

            AssertEx.True(result.Completed, $"the probe command must run: {result.StandardError}");
            AssertEx.Contains(result.StandardOutput, "[]", StringComparison.Ordinal,
                "a variable outside the provider's allow-list must not reach the child");
            AssertEx.False(result.StandardOutput.Contains("sentinel-secret", StringComparison.Ordinal),
                "the worker's environment is scrubbed, which is a real control even though the filesystem is not confined");
        }
        finally
        {
            Environment.SetEnvironmentVariable(SecretName, previous);
        }
    }

    private static void SkipUnlessPosixJail()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip.Test("BLOCKED: these probes spawn POSIX utilities in the process jail and need Linux. "
                      + "On Windows the provider applies NO wrapper at all (setsid/systemd-run/unshare are Linux-only), "
                      + "so the child is a plain process with the host's filesystem, network and no ceilings.");
        }
    }

    private static ProcessSandboxRuntimeProvider CreateProvider()
    {
        return new ProcessSandboxRuntimeProvider(Options.Create(new LocalContainerOptions()), TimeProvider.System);
    }

    /// <summary>Creates a sandbox exactly as <c>AgentHomeService.PrepareUnderLeaseAsync</c> does: no isolation mode named.</summary>
    private static async Task<SandboxHandle> CreateAgentHomeSandboxAsync(ProcessSandboxRuntimeProvider provider)
    {
        var handle = await provider.CreateOrAttachAsync(new SandboxCreateRequest
        {
            AttachKey = AttachKey("owner-reach", "node-reach"),
            RuntimeProfile = "dotnet-agent-home",
            // The real provider fails closed on a network posture it cannot enforce, so ask for what every host has.
            NetworkPolicy = SandboxNetworkPolicy.Unrestricted
        });

        // The workspace root has to exist for the working directory to resolve; AgentHome's copy step creates it.
        await provider.ResetDirectoryAsync(handle, AgentHomeGit.WorkspaceSelectedRoot);
        return handle;
    }

    private static SandboxAttachKey AttachKey(string owner, string node)
    {
        return new SandboxAttachKey
        {
            OwnerUserId = owner,
            NodeId = node,
            ProviderName = ProcessSandboxRuntimeProvider.Name,
            RuntimeProfile = "dotnet-agent-home",
            ManifestVersion = AgentHomeManifest.CurrentVersion
        };
    }

    private string CreateTempDirectory(string prefix)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        _tempPaths.Add(directory);
        return directory;
    }
}
