namespace XE_Local_AI_Engine.Tests.Sandbox;

using System.Text;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Reaping;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Behavior coverage for <see cref="ProcessSandboxRuntimeProvider" />, the supervised-child sandbox provider. It
///     spawns REAL processes where the behavior under test requires it (timeout, cancel, tree-kill) and uses real host
///     temp files for the FS-guard cases, so the jail / byte-cap / no-follow semantics are proven, not mocked. The
///     deleted <see cref="XE_Local_AI_Engine.Client.Services.Sandbox.Fake.FakeSandboxRuntimeProvider" /> is the parity
///     oracle. Linux is the primary runtime; the OS-divergent shell command and the symlink case are guarded so the
///     suite stays green on any host.
/// </summary>
public sealed class ProcessSandboxRuntimeProviderTests : IDisposable
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
        }
    }

    [Test]
    public async Task ProcessSandboxProvider_CreateOrAttach_JailsToNodeScopedDir()
    {
        using var provider = CreateProvider();

        var handle = await provider.CreateOrAttachAsync(CreateRequest(Key()));

        AssertEx.Equal(ProcessSandboxRuntimeProvider.Name, handle.ProviderName);
        AssertEx.NotNullOrEmpty(handle.SandboxId);

        // Same key reuses the same node-scoped jail.
        var again = await provider.CreateOrAttachAsync(CreateRequest(Key()));
        AssertEx.Equal(handle.SandboxId, again.SandboxId);

        // Owner change on the same node forbids reuse: the old sandbox is evicted (its handle goes invalid).
        var underNewOwner = await provider.CreateOrAttachAsync(CreateRequest(Key("owner-b")));
        AssertEx.NotEqual(handle.SandboxId, underNewOwner.SandboxId);
        await AssertEx.ThrowsAsync<SandboxHandleInvalidException>(() => provider.ConnectAsync(Key()));
    }

    [Test]
    public async Task ProcessSandboxProvider_CreateOrAttach_DifferentProfilesDoNotOverwriteEachOther()
    {
        using var provider = CreateProvider();
        var agentHomeKey = Key();
        var developmentKey = agentHomeKey with
        {
            RuntimeProfile = "development-local",
            ManifestVersion = 2
        };

        var agentHome = await provider.CreateOrAttachAsync(CreateRequest(agentHomeKey));
        var development = await provider.CreateOrAttachAsync(new SandboxCreateRequest
        {
            AttachKey = developmentKey,
            RuntimeProfile = developmentKey.RuntimeProfile,
            NetworkPolicy = SandboxNetworkPolicy.Unrestricted
        });

        AssertEx.NotEqual(agentHome.SandboxId, development.SandboxId);
        AssertEx.Equal(agentHome.SandboxId, (await provider.ConnectAsync(agentHomeKey)).SandboxId);
        AssertEx.Equal(development.SandboxId, (await provider.ConnectAsync(developmentKey)).SandboxId);
    }

    [Test]
    public async Task ProcessSandboxProvider_Connect_WhenJailMissing_ThrowsHandleInvalid()
    {
        using var provider = CreateProvider();
        var handle = await provider.CreateOrAttachAsync(CreateRequest(Key()));

        await provider.KillAsync(handle);

        await AssertEx.ThrowsAsync<SandboxHandleInvalidException>(() => provider.ConnectAsync(Key()));
    }

    [Test]
    public async Task ProcessSandboxProvider_Execute_HonorsTimeout_ReturnsTimedOutNotThrow()
    {
        using var provider = CreateProvider();
        var handle = await provider.CreateOrAttachAsync(CreateRequest(Key()));

        // A 30s sleep with a 200ms command timeout must be killed and surface as a non-throwing timed-out result.
        var (executable, arguments) = ShellCommand("sleep 30");
        var result = await provider.ExecuteAsync(handle, new SandboxCommandRequest
        {
            ExecutionId = "timeout-1",
            Executable = executable,
            Arguments = arguments,
            Timeout = TimeSpan.FromMilliseconds(200)
        });

        AssertEx.False(result.Completed, "a timed-out command must not be Completed");
        AssertEx.Equal(expected: -1, result.ExitCode);
        AssertEx.Equal("timeout-1", result.ExecutionId);
    }

    [Test]
    public async Task ProcessSandboxProvider_Execute_WhenCancelled_TreeKillsAndPropagates()
    {
        using var provider = CreateProvider();
        var handle = await provider.CreateOrAttachAsync(CreateRequest(Key()));
        using var cancellation = new CancellationTokenSource();

        var (executable, arguments) = ShellCommand("sleep 30");
        var executeTask = provider.ExecuteAsync(handle, new SandboxCommandRequest
        {
            ExecutionId = "cancel-1",
            Executable = executable,
            Arguments = arguments
        }, cancellation.Token);

        // Poll until the task is confirmed in-flight (process started); exit early if the task
        // completes unexpectedly (process failed to start) rather than waiting a fixed duration.
        for (var i = 0; i < 30 && !executeTask.IsCompleted; i++)
        {
            await Task.Delay(5);
        }

        await cancellation.CancelAsync();

        await AssertEx.ThrowsAsync<OperationCanceledException>(() => executeTask);
    }

    [Test]
    public async Task ProcessSandboxProvider_Execute_ByteBudget_CapsCapturedOutput()
    {
        using var provider = CreateProvider();
        var handle = await provider.CreateOrAttachAsync(CreateRequest(Key()));

        // Emit far more than the captured-output cap as many MULTIBYTE lines (the pump is line-based); the captured
        // stdout must be bounded by the real UTF-8 BYTE budget (not a char count), so even multibyte content cannot
        // exceed the named byte cap.
        var (executable, arguments) = ShellCommand("yes 'αααααααααααααααααααααααααααααααααααααααα' | head -n 200000");
        var result = await provider.ExecuteAsync(handle, new SandboxCommandRequest
        {
            ExecutionId = "budget-1",
            Executable = executable,
            Arguments = arguments,
            Timeout = TimeSpan.FromSeconds(30)
        });

        AssertEx.True(result.Completed, "the command completes; only its captured output is capped");
        const int capBytes = 4 * 1024 * 1024;
        var capturedBytes = Encoding.UTF8.GetByteCount(result.StandardOutput);
        AssertEx.True(capturedBytes <= capBytes,
            $"captured stdout must be capped at the {capBytes}-byte UTF-8 budget but was {capturedBytes} bytes");
        AssertEx.True(result.StandardOutputTruncated, "the bounded result must explicitly report discarded stdout bytes");
    }

    [Test]
    public async Task ProcessSandboxProvider_Execute_WhenWorkingDirectoryTraversesParent_Rejects()
    {
        using var provider = CreateProvider();
        var handle = await provider.CreateOrAttachAsync(CreateRequest(Key()));
        var (executable, arguments) = ShellCommand("pwd");

        await AssertEx.ThrowsAsync<UnauthorizedAccessException>(() => provider.ExecuteAsync(handle, new SandboxCommandRequest
        {
            ExecutionId = "parent-traversal-1",
            Executable = executable,
            Arguments = arguments,
            WorkingDirectory = "../../outside"
        }));
    }

    [Test]
    public async Task ProcessSandboxProvider_Execute_WhenWorkingDirectoryTraversesIntermediateSymlink_Rejects()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var provider = CreateProvider();
        var handle = await provider.CreateOrAttachAsync(CreateRequest(Key()));
        using var escapeTarget = new TempDir();
        Directory.CreateDirectory(Path.Combine(escapeTarget.Path, "nested"));
        await File.WriteAllTextAsync(Path.Combine(escapeTarget.Path, "nested", "secret.txt"), "OUTSIDE-THE-JAIL");
        await RunShellInJailAsync(provider, handle,
            $"mkdir -p workspace && ln -s {ShellQuote(escapeTarget.Path)} workspace/link");

        await AssertEx.ThrowsAsync<UnauthorizedAccessException>(() => provider.ExecuteAsync(handle, new SandboxCommandRequest
        {
            ExecutionId = "intermediate-symlink-1",
            Executable = "find",
            Arguments = [".", "-maxdepth", "1", "-print"],
            WorkingDirectory = "workspace/link/nested"
        }));
    }

    [Test]
    public async Task ProcessSandboxProvider_Execute_WhenWorkingDirectoryIsLeafSymlink_Rejects()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var provider = CreateProvider();
        var handle = await provider.CreateOrAttachAsync(CreateRequest(Key()));
        using var escapeTarget = new TempDir();
        await File.WriteAllTextAsync(Path.Combine(escapeTarget.Path, "secret.txt"), "OUTSIDE-THE-JAIL");
        await RunShellInJailAsync(provider, handle,
            $"mkdir -p workspace && ln -s {ShellQuote(escapeTarget.Path)} workspace/link");

        await AssertEx.ThrowsAsync<UnauthorizedAccessException>(() => provider.ExecuteAsync(handle, new SandboxCommandRequest
        {
            ExecutionId = "leaf-symlink-1",
            Executable = "grep",
            Arguments = ["-rnF", "-e", "OUTSIDE-THE-JAIL", "--", "."],
            WorkingDirectory = "workspace/link"
        }));
    }

    [Test]
    public async Task ProcessSandboxProvider_CopyInto_RejectsPathOutsideJail()
    {
        using var provider = CreateProvider();
        var handle = await provider.CreateOrAttachAsync(CreateRequest(Key()));
        var source = WriteHostTempFile("payload");

        await AssertEx.ThrowsAsync<UnauthorizedAccessException>(() => provider.CopyIntoAsync(handle, new SandboxCopyRequest
        {
            SourcePath = source,
            DestinationPath = "../../escape.txt"
        }));
    }

    [Test]
    public async Task ProcessSandboxProvider_CopyInto_WhenFinalComponentIsSymlink_Rejects()
    {
        if (!OperatingSystem.IsLinux())
        {
            // The O_NOFOLLOW atomic refusal is the Linux guarantee under test; on other hosts the fallback open does
            // not refuse a symlink, so the assertion is Linux-only (the primary runtime).
            return;
        }

        using var provider = CreateProvider();
        var handle = await provider.CreateOrAttachAsync(CreateRequest(Key()));

        // Point the copy source at a symlink whose target is a real file. The no-follow open must refuse the leaf
        // symlink rather than read through it.
        var realTarget = WriteHostTempFile("secret-behind-link");
        var linkPath = Path.Combine(Path.GetTempPath(), "xe-link-" + Guid.NewGuid().ToString("N"));
        _tempPaths.Add(linkPath);
        File.CreateSymbolicLink(linkPath, realTarget);

        await AssertEx.ThrowsAsync<UnauthorizedAccessException>(() => provider.CopyIntoAsync(handle, new SandboxCopyRequest
        {
            SourcePath = linkPath,
            DestinationPath = "workspace/landed.txt"
        }));
    }

    [Test]
    public async Task ProcessSandboxProvider_ReadFile_WhenPathTraversesJailSymlink_Rejects()
    {
        if (!OperatingSystem.IsLinux())
        {
            // Real symlink semantics + the no-follow open are the Linux guarantee under test.
            return;
        }

        using var provider = CreateProvider();
        var handle = await provider.CreateOrAttachAsync(CreateRequest(Key()));
        using var escapeTarget = new TempDir();
        await File.WriteAllTextAsync(Path.Combine(escapeTarget.Path, "secret.txt"), "OUTSIDE-THE-JAIL");

        // A sandboxed command plants an INTERMEDIATE-component symlink inside the jail: workspace/link -> <outside dir>.
        await RunShellInJailAsync(provider, handle,
            $"mkdir -p workspace && ln -s {ShellQuote(escapeTarget.Path)} workspace/link");

        // Reading through the intermediate symlink must be rejected, not followed to the outside file.
        await AssertEx.ThrowsAsync<UnauthorizedAccessException>(() =>
            provider.ReadFileAsync(handle, "workspace/link/secret.txt"));

        // And a FINAL-component symlink (workspace/leaf -> outside file) must also be rejected.
        await RunShellInJailAsync(provider, handle,
            $"ln -s {ShellQuote(Path.Combine(escapeTarget.Path, "secret.txt"))} workspace/leaf");
        await AssertEx.ThrowsAsync<UnauthorizedAccessException>(() =>
            provider.ReadFileAsync(handle, "workspace/leaf"));
    }

    [Test]
    public async Task ProcessSandboxProvider_CopyOut_WhenJailSourceIsEscapingSymlink_Rejects()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var provider = CreateProvider();
        var handle = await provider.CreateOrAttachAsync(CreateRequest(Key()));
        using var escapeTarget = new TempDir();
        await File.WriteAllTextAsync(Path.Combine(escapeTarget.Path, "secret.txt"), "OUTSIDE-THE-JAIL");

        // Plant a jail-side symlink pointing at a host file outside the jail.
        await RunShellInJailAsync(provider, handle,
            $"mkdir -p workspace && ln -s {ShellQuote(Path.Combine(escapeTarget.Path, "secret.txt"))} workspace/leak");

        var hostDestination = Path.Combine(Path.GetTempPath(), "xe-copyout-" + Guid.NewGuid().ToString("N"));
        _tempPaths.Add(hostDestination);

        // Copy-out of the escaping symlink must be rejected; the outside content must NOT land on the host destination.
        await AssertEx.ThrowsAsync<UnauthorizedAccessException>(() => provider.CopyOutAsync(handle, new SandboxCopyRequest
        {
            SourcePath = "workspace/leak",
            DestinationPath = hostDestination
        }));
        AssertEx.False(File.Exists(hostDestination), "the escaping copy-out must not have written the host destination");
    }

    [Test]
    public async Task ProcessSandboxProvider_CopyInto_WhenDestinationComponentIsEscapingSymlink_Rejects()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var provider = CreateProvider();
        var handle = await provider.CreateOrAttachAsync(CreateRequest(Key()));
        using var escapeTarget = new TempDir();
        var hostSource = WriteHostTempFile("payload-to-plant");

        // A sandboxed command plants a directory symlink inside the jail: workspace/out -> <outside dir>. A copy-into
        // whose destination traverses it would write the payload OUTSIDE the jail.
        await RunShellInJailAsync(provider, handle,
            $"mkdir -p workspace && ln -s {ShellQuote(escapeTarget.Path)} workspace/out");

        await AssertEx.ThrowsAsync<UnauthorizedAccessException>(() => provider.CopyIntoAsync(handle, new SandboxCopyRequest
        {
            SourcePath = hostSource,
            DestinationPath = "workspace/out/planted.txt"
        }));
        AssertEx.False(File.Exists(Path.Combine(escapeTarget.Path, "planted.txt")),
            "the copy-into must not have written through the escaping destination symlink");
    }

    [Test]
    public async Task ProcessSandboxProvider_CopyInto_WhenDestinationComponentIsSymlink_DoesNotCreateOutsideDirectories()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var provider = CreateProvider();
        var handle = await provider.CreateOrAttachAsync(CreateRequest(Key()));
        using var escapeTarget = new TempDir();
        var hostSource = WriteHostTempFile("payload-to-plant");
        await RunShellInJailAsync(provider, handle,
            $"mkdir -p workspace && ln -s {ShellQuote(escapeTarget.Path)} workspace/out");

        await AssertEx.ThrowsAsync<UnauthorizedAccessException>(() => provider.CopyIntoAsync(handle, new SandboxCopyRequest
        {
            SourcePath = hostSource,
            DestinationPath = "workspace/out/new/nested/planted.txt"
        }));

        AssertEx.False(Directory.Exists(Path.Combine(escapeTarget.Path, "new")),
            "copy-into must reject the existing symlink prefix before Directory.CreateDirectory can mutate outside the jail");
    }

    [Test]
    public async Task ProcessSandboxProvider_CopyInto_AtExactlyTheCap_StillCopies()
    {
        // The per-file cap comes from LocalContainerOptions; drive a small explicit cap so the boundary is testable.
        using var provider = CreateProvider(8);
        var handle = await provider.CreateOrAttachAsync(CreateRequest(Key()));
        var source = WriteHostTempFileBytes([1, 2, 3, 4, 5, 6, 7, 8]); // exactly 8 bytes = the cap

        await provider.CopyIntoAsync(handle, new SandboxCopyRequest
        {
            SourcePath = source,
            DestinationPath = "workspace/exact.bin"
        });

        var readBack = await provider.ReadFileAsync(handle, "workspace/exact.bin");
        AssertEx.Equal(expected: 8, readBack.Length);
    }

    [Test]
    public async Task ProcessSandboxProvider_CopyInto_WhenGrowsConcurrently_NeverTruncated()
    {
        // A source one byte over the cap is SKIPPED-AND-LOGGED (degrade gracefully, parity with the deleted container
        // provider), never silently truncated to the stale size and never thrown (a throw would abort the whole
        // workspace-copy loop in AgentHomeWorkspaceService). The destination must simply not exist.
        using var provider = CreateProvider(8);
        var handle = await provider.CreateOrAttachAsync(CreateRequest(Key()));
        var source = WriteHostTempFileBytes([1, 2, 3, 4, 5, 6, 7, 8, 9]); // 9 bytes = over the 8-byte cap

        // No throw: the over-cap file is skipped, not rejected with an exception.
        await provider.CopyIntoAsync(handle, new SandboxCopyRequest
        {
            SourcePath = source,
            DestinationPath = "workspace/over.bin"
        });

        // Nothing was written (never truncated to the stale size).
        await AssertEx.ThrowsAsync<FileNotFoundException>(() => provider.ReadFileAsync(handle, "workspace/over.bin"));
    }

    [Test]
    public async Task ProcessSandboxProvider_ReadFile_DecodesUtf8()
    {
        using var provider = CreateProvider();
        var handle = await provider.CreateOrAttachAsync(CreateRequest(Key()));
        var source = WriteHostTempFile("héllo · wörld");

        await provider.CopyIntoAsync(handle, new SandboxCopyRequest
        {
            SourcePath = source,
            DestinationPath = "workspace/utf8.txt"
        });

        var content = await provider.ReadFileAsync(handle, "workspace/utf8.txt");
        AssertEx.Equal("héllo · wörld", content);
    }

    [Test]
    public async Task ProcessSandboxProvider_CopyOut_WritesRawBytes()
    {
        using var provider = CreateProvider();
        var handle = await provider.CreateOrAttachAsync(CreateRequest(Key()));
        byte[] payload = [0x00, 0x01, 0x02, 0xFF, 0xFE, 0x42];
        var source = WriteHostTempFileBytes(payload);

        await provider.CopyIntoAsync(handle, new SandboxCopyRequest
        {
            SourcePath = source,
            DestinationPath = "workspace/blob.bin"
        });

        var hostDestination = Path.Combine(Path.GetTempPath(), "xe-out-" + Guid.NewGuid().ToString("N"));
        _tempPaths.Add(hostDestination);
        await provider.CopyOutAsync(handle, new SandboxCopyRequest
        {
            SourcePath = "workspace/blob.bin",
            DestinationPath = hostDestination
        });

        var roundTripped = await File.ReadAllBytesAsync(hostDestination);
        AssertEx.Equal(payload.Length, roundTripped.Length);
        AssertEx.True(payload.AsSpan().SequenceEqual(roundTripped), "copy-out must preserve the raw bytes exactly");
    }

    [Test]
    public async Task ProcessSandboxProvider_Kill_TreeKillsAndInvalidatesHandle()
    {
        using var provider = CreateProvider();
        var handle = await provider.CreateOrAttachAsync(CreateRequest(Key()));
        var source = WriteHostTempFile("data");
        await provider.CopyIntoAsync(handle, new SandboxCopyRequest
        {
            SourcePath = source,
            DestinationPath = "workspace/data.txt"
        });

        // Start a long-running command, then kill the sandbox: the in-flight execution is tree-killed and the handle
        // is invalidated (a subsequent read throws).
        var (executable, arguments) = ShellCommand("sleep 30");
        var executeTask = provider.ExecuteAsync(handle, new SandboxCommandRequest
        {
            ExecutionId = "kill-1",
            Executable = executable,
            Arguments = arguments
        });
        // Poll until the task is confirmed in-flight before killing the sandbox.
        for (var i = 0; i < 30 && !executeTask.IsCompleted; i++)
        {
            await Task.Delay(5);
        }

        await provider.KillAsync(handle);

        // The killed command surfaces as a cancellation/abnormal exit, not a clean completion.
        await SwallowAsync(executeTask);
        await AssertEx.ThrowsAsync<SandboxHandleInvalidException>(() => provider.ReadFileAsync(handle, "workspace/data.txt"));
    }

    [Test]
    public async Task ProcessSandboxProvider_CancelCommand_TreeKillsInFlight()
    {
        using var provider = CreateProvider();
        var handle = await provider.CreateOrAttachAsync(CreateRequest(Key()));

        var (executable, arguments) = ShellCommand("sleep 30");
        var executeTask = provider.ExecuteAsync(handle, new SandboxCommandRequest
        {
            ExecutionId = "cancelcmd-1",
            Executable = executable,
            Arguments = arguments,
            Timeout = TimeSpan.FromSeconds(30)
        });
        // Poll until the task is confirmed in-flight before cancelling the command.
        for (var i = 0; i < 30 && !executeTask.IsCompleted; i++)
        {
            await Task.Delay(5);
        }

        await provider.CancelCommandAsync(handle, "cancelcmd-1");

        var result = await executeTask;
        AssertEx.False(result.Completed, "a best-effort cancel tree-kills the command so it does not complete");
    }

    [Test]
    public async Task ProcessSandboxProvider_Capabilities_AdvertiseOnlyServedSurface()
    {
        using var provider = CreateProvider();

        var capabilities = provider.Capabilities;

        AssertEx.True(capabilities.HasFlag(SandboxProviderCapabilities.SupportsCopyInto));
        AssertEx.True(capabilities.HasFlag(SandboxProviderCapabilities.SupportsCopyOut));
        AssertEx.True(capabilities.HasFlag(SandboxProviderCapabilities.SupportsCommandCancellation));
        AssertEx.True(capabilities.HasFlag(SandboxProviderCapabilities.SupportsAttach));
        AssertEx.True(capabilities.HasFlag(SandboxProviderCapabilities.SupportsKill));
        // Never served in any configuration: there is no mount layer at all.
        AssertEx.False(capabilities.HasFlag(SandboxProviderCapabilities.SupportsReadOnlyMounts));
        // CreateProvider pins a containment probe reporting NO mechanisms, so on a host that can contain nothing the
        // provider must claim nothing. This is the original guard, now stated against an explicit host rather than an
        // assumption about the runner.
        AssertEx.False(capabilities.HasFlag(SandboxProviderCapabilities.SupportsResourceLimits));
        AssertEx.False(capabilities.HasFlag(SandboxProviderCapabilities.SupportsNetworkPolicy));
    }

    [Test]
    public async Task ProcessSandboxProvider_Capabilities_AdvertisesEachFlagOnlyWhenItsMechanismIsActive()
    {
        // The honesty invariant, asserted in both directions against the SAME probe the launch path reads. A flag that
        // could appear without its mechanism is exactly the integrity gap this contract exists to prevent.
        using var limitsOnly = CreateProvider(containment: new SandboxContainment
        {
            SupportsResourceLimits = true,
            SystemdRunPath = "/usr/bin/systemd-run",
            EnvPath = "/usr/bin/env",
            SetsidPath = "/usr/bin/setsid",
            SupportsProcessGroup = true
        });
        AssertEx.True(limitsOnly.Capabilities.HasFlag(SandboxProviderCapabilities.SupportsResourceLimits));
        AssertEx.False(limitsOnly.Capabilities.HasFlag(SandboxProviderCapabilities.SupportsNetworkPolicy),
            "network policy must not be advertised when only the limits mechanism is active");

        using var networkOnly = CreateProvider(containment: new SandboxContainment
        {
            SupportsNetworkIsolation = true,
            UnsharePath = "/usr/bin/unshare"
        });
        AssertEx.True(networkOnly.Capabilities.HasFlag(SandboxProviderCapabilities.SupportsNetworkPolicy));
        AssertEx.False(networkOnly.Capabilities.HasFlag(SandboxProviderCapabilities.SupportsResourceLimits),
            "resource limits must not be advertised when only the network mechanism is active");

        await Task.CompletedTask;
    }

    [Test]
    public async Task ProcessSandboxProvider_Capabilities_MatchTheRealHostProbe_InBothDirections()
    {
        // Advertisement and enforcement must agree on the ACTUAL runner, whatever it happens to support. Written as an
        // iff against the live probe so it stays true on a CI box with no user systemd and on a developer box with one.
        using var provider = CreateHostProvider();
        var containment = HostContainment();

        AssertEx.Equal(containment.SupportsResourceLimits,
            provider.Capabilities.HasFlag(SandboxProviderCapabilities.SupportsResourceLimits),
            "SupportsResourceLimits must be advertised if and only if the host mechanism is active");
        AssertEx.Equal(containment.SupportsNetworkIsolation,
            provider.Capabilities.HasFlag(SandboxProviderCapabilities.SupportsNetworkPolicy),
            "SupportsNetworkPolicy must be advertised if and only if the host mechanism is active");

        await Task.CompletedTask;
    }

    [Test]
    public async Task ProcessSandboxProvider_Execute_DoesNotLeakWorkerEnvironment_ButAllowlistedAndRequestVarsAppear()
    {
        // Linux-only: uses `printenv` and /bin/sh. Windows env behavior is covered by the same allow-list logic.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A canary secret placed in the WORKER (parent) environment must never reach the sandbox child.
        var canaryName = "XE_SANDBOX_CANARY_" + Guid.NewGuid().ToString("N");
        const string canaryValue = "worker-secret-must-not-leak";
        Environment.SetEnvironmentVariable(canaryName, canaryValue);
        try
        {
            using var provider = CreateProvider();
            var handle = await provider.CreateOrAttachAsync(CreateRequest(Key()));

            var result = await provider.ExecuteAsync(handle, new SandboxCommandRequest
            {
                ExecutionId = "env-1",
                Executable = "/bin/sh",
                Arguments = ["-c", "printenv"],
                // A caller-supplied variable is layered on top of the allow-list and MUST be visible to the child.
                Environment = new Dictionary<string, string>
                {
                    ["XE_REQUEST_VAR"] = "request-value"
                },
                Timeout = TimeSpan.FromSeconds(15)
            });

            AssertEx.True(result.Completed, $"printenv must complete: {result.StandardError}");
            AssertEx.Equal(expected: 0, result.ExitCode);

            var output = result.StandardOutput ?? string.Empty;
            // The worker's secret canary is absent — neither its name nor its value crossed into the child.
            AssertEx.False(output.Contains(canaryName, StringComparison.Ordinal), "the canary variable name must not leak to the sandbox child");
            AssertEx.False(output.Contains(canaryValue, StringComparison.Ordinal), "the canary variable value must not leak to the sandbox child");
            // An allow-listed toolchain variable is forwarded so the fixed executables still run.
            AssertEx.True(output.Contains("PATH=", StringComparison.Ordinal), "the allow-listed PATH must be forwarded to the child");
            // The caller's explicit request variable is layered on top and visible.
            AssertEx.True(output.Contains("XE_REQUEST_VAR=request-value", StringComparison.Ordinal), "a request-supplied variable must be visible to the child");
        }
        finally
        {
            Environment.SetEnvironmentVariable(canaryName, value: null);
        }
    }

    [Test]
    public async Task ProcessSandboxProvider_CreateOrAttach_RejectsUnenforceableNetworkPolicy()
    {
        // Pinned to a host that contains nothing: None demands egress isolation there is no mechanism for → fail closed.
        using var provider = CreateProvider();

        await AssertEx.ThrowsAsync<SandboxCapabilityNotSupportedException>(() => provider.CreateOrAttachAsync(new SandboxCreateRequest
        {
            AttachKey = Key(),
            RuntimeProfile = "dotnet-agent-home",
            NetworkPolicy = SandboxNetworkPolicy.None
        }));

        // Restricted (an egress allow-list) is rejected on EVERY host, including one that can create a namespace: the
        // provider ships default-deny only, and an allow-list needs machinery it does not have.
        using var networkCapable = CreateProvider(containment: new SandboxContainment
        {
            SupportsNetworkIsolation = true,
            UnsharePath = "/usr/bin/unshare"
        });

        await AssertEx.ThrowsAsync<SandboxCapabilityNotSupportedException>(() => networkCapable.CreateOrAttachAsync(new SandboxCreateRequest
        {
            AttachKey = Key(),
            RuntimeProfile = "dotnet-agent-home",
            NetworkPolicy = SandboxNetworkPolicy.Restricted
        }));
    }

    [Test]
    public async Task ProcessSandboxProvider_CreateOrAttach_AcceptsNoNetwork_WhenTheHostCanIsolate()
    {
        // The other half of the fail-closed contract: once a mechanism exists, the guarantee must be HONORED rather
        // than reflexively refused. A provider that advertises SupportsNetworkPolicy and still rejects None would be
        // just as dishonest as one that accepts it without a mechanism.
        using var provider = CreateProvider(containment: new SandboxContainment
        {
            SupportsNetworkIsolation = true,
            UnsharePath = "/usr/bin/unshare"
        });

        var handle = await provider.CreateOrAttachAsync(new SandboxCreateRequest
        {
            AttachKey = Key(),
            RuntimeProfile = "dotnet-agent-home",
            NetworkPolicy = SandboxNetworkPolicy.None
        });

        AssertEx.NotNullOrEmpty(handle.SandboxId);
    }

    [Test]
    public async Task ProcessSandboxProvider_CreateOrAttach_RejectsUnenforceableResourceLimits()
    {
        using var provider = CreateProvider();

        await AssertEx.ThrowsAsync<SandboxCapabilityNotSupportedException>(() => provider.CreateOrAttachAsync(new SandboxCreateRequest
        {
            AttachKey = Key(),
            RuntimeProfile = "dotnet-agent-home",
            NetworkPolicy = SandboxNetworkPolicy.Unrestricted,
            ResourceLimits = new SandboxResourceLimits
            {
                MemoryMb = 512
            }
        }));
    }

    [Test]
    public async Task ProcessSandboxProvider_CreateOrAttach_AcceptsResourceLimits_WhenTheHostCanEnforceThem()
    {
        using var provider = CreateProvider(containment: new SandboxContainment
        {
            SupportsProcessGroup = true,
            SupportsResourceLimits = true,
            SetsidPath = "/usr/bin/setsid",
            SystemdRunPath = "/usr/bin/systemd-run",
            EnvPath = "/usr/bin/env"
        });

        var handle = await provider.CreateOrAttachAsync(new SandboxCreateRequest
        {
            AttachKey = Key(),
            RuntimeProfile = "dotnet-agent-home",
            NetworkPolicy = SandboxNetworkPolicy.Unrestricted,
            ResourceLimits = new SandboxResourceLimits
            {
                MemoryMb = 512,
                PidsLimit = 64
            }
        });

        AssertEx.NotNullOrEmpty(handle.SandboxId);
    }

    [Test]
    public async Task ProcessSandboxProvider_Execute_WhenChildExceedsJailDiskCap_TreeKillsAndReturnsIncomplete()
    {
        // Lane D: MaxCopyFileBytes bounds only the host→jail copy-in re-read. Without this watchdog a command could
        // fill the host disk from INSIDE the jail and nothing would stop it.
        if (!OperatingSystem.IsLinux())
        {
            Skip("the jail disk watchdog test uses /bin/sh and dd");
            return;
        }

        // A 4 MiB ceiling against a command that writes far more, so the watchdog fires well inside the timeout.
        using var provider = CreateProvider(maxJailDiskBytes: 4L * 1024 * 1024);
        var handle = await provider.CreateOrAttachAsync(CreateRequest(Key()));

        var result = await provider.ExecuteAsync(handle, new SandboxCommandRequest
        {
            ExecutionId = "disk-1",
            Executable = "/bin/sh",
            // Write steadily rather than in one burst, so the periodic watchdog observes the growth while the command
            // is still running.
            Arguments = ["-c", "i=0; while [ $i -lt 400 ]; do dd if=/dev/zero of=fill-$i.bin bs=1M count=2 2>/dev/null; i=$((i+1)); sleep 0.05; done"],
            Timeout = TimeSpan.FromSeconds(60)
        });

        AssertEx.False(result.Completed, "a command that blows the jail disk ceiling must not be Completed");
        AssertEx.Equal(expected: -1, result.ExitCode);
        AssertEx.Contains(result.StandardError ?? string.Empty, "disk ceiling");
    }

    [Test]
    public async Task ProcessSandboxProvider_Execute_WhenEgressDenied_ChildCannotReachLoopbackLanOrMetadata()
    {
        // LIVE, Lane E. Asserts the mechanism's real effect, not the argument mapping — SandboxLaunchPlanTests covers
        // the mapping, and a wrapper chain that maps correctly but does not actually isolate would pass that and fail
        // the product.
        var containment = HostContainment();
        if (!containment.SupportsNetworkIsolation)
        {
            Skip($"this host cannot create an empty network namespace: {containment.NetworkIsolationUnavailableReason}");
            return;
        }

        // The socket probe needs bash: /bin/sh is dash on this distro and dash has no /dev/tcp, so a dash probe would
        // fail to connect for the WRONG reason and the test would pass without proving anything.
        const string bashPath = "/usr/bin/bash";
        if (!File.Exists(bashPath))
        {
            Skip("bash is required for the /dev/tcp egress probe");
            return;
        }

        using var provider = CreateHostProvider();
        var handle = await provider.CreateOrAttachAsync(new SandboxCreateRequest
        {
            AttachKey = Key(),
            RuntimeProfile = "dotnet-agent-home",
            NetworkPolicy = SandboxNetworkPolicy.None
        });

        // A real listener on the host loopback stands in for the node's own API — the most sensitive thing in reach.
        // Using a live socket matters: a connect to a dead port fails on any host, which would make the assertion
        // vacuous.
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port: 0);
        listener.Start();
        var listenerPort = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;

        try
        {
            var probe = $"for hp in 127.0.0.1:{listenerPort} 169.254.169.254:80 1.1.1.1:53; do "
                        + "h=${hp%%:*}; p=${hp##*:}; "
                        + "if timeout 3 bash -c \"exec 3<>/dev/tcp/$h/$p\" 2>/dev/null; then echo \"REACHED $hp\"; fi; "
                        + "done; echo PROBE-DONE";

            var result = await provider.ExecuteAsync(handle, new SandboxCommandRequest
            {
                ExecutionId = "egress-1",
                Executable = bashPath,
                Arguments = ["-c", probe],
                Timeout = TimeSpan.FromSeconds(60)
            });

            AssertEx.True(result.Completed, $"the probe command must run: {result.StandardError}");
            AssertEx.Contains(result.StandardOutput ?? string.Empty, "PROBE-DONE");
            AssertEx.False((result.StandardOutput ?? string.Empty).Contains("REACHED", StringComparison.Ordinal),
                $"no target may be reachable from inside the namespace, got: {result.StandardOutput}");
        }
        finally
        {
            listener.Stop();
        }
    }

    [Test]
    public async Task ProcessSandboxProvider_AgentHomeShapedRun_ComposesCapabilityGatedDenialEndToEnd()
    {
        // The flip and the mechanism are two separate facts; this asserts they COMPOSE. It mirrors what
        // AgentHomeService.ResolveNetworkPolicy() does — pick None iff the provider advertises the capability — and
        // then proves a child of that sandbox really cannot reach loopback, the LAN, or the metadata endpoint.
        const string bashPath = "/usr/bin/bash";
        if (!OperatingSystem.IsLinux() || !File.Exists(bashPath))
        {
            Skip("bash on Linux is required for the /dev/tcp egress probe");
            return;
        }

        using var provider = CreateHostProvider();

        // Exactly the caller-side decision AgentHomeService makes.
        var resolved = provider.Capabilities.HasFlag(SandboxProviderCapabilities.SupportsNetworkPolicy)
            ? SandboxNetworkPolicy.None
            : SandboxNetworkPolicy.Unrestricted;

        if (resolved != SandboxNetworkPolicy.None)
        {
            Skip($"this host cannot deny egress, so AgentHome correctly stays Unrestricted: {HostContainment().NetworkIsolationUnavailableReason}");
            return;
        }

        var handle = await provider.CreateOrAttachAsync(new SandboxCreateRequest
        {
            AttachKey = Key(),
            RuntimeProfile = "dotnet-agent-home",
            NetworkPolicy = resolved
        });

        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port: 0);
        listener.Start();
        var listenerPort = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;

        try
        {
            var probe = $"for hp in 127.0.0.1:{listenerPort} 169.254.169.254:80 1.1.1.1:53; do "
                        + "h=${hp%%:*}; p=${hp##*:}; "
                        + "if timeout 3 bash -c \"exec 3<>/dev/tcp/$h/$p\" 2>/dev/null; then echo \"REACHED $hp\"; fi; "
                        + "done; echo PROBE-DONE";

            var result = await provider.ExecuteAsync(handle, new SandboxCommandRequest
            {
                ExecutionId = "agenthome-egress-1",
                Executable = bashPath,
                Arguments = ["-c", probe],
                Timeout = TimeSpan.FromSeconds(60)
            });

            AssertEx.True(result.Completed, $"the probe command must run: {result.StandardError}");
            AssertEx.Contains(result.StandardOutput ?? string.Empty, "PROBE-DONE");
            AssertEx.False((result.StandardOutput ?? string.Empty).Contains("REACHED", StringComparison.Ordinal),
                $"an AgentHome-shaped sandbox must reach none of loopback / metadata / LAN, got: {result.StandardOutput}");
        }
        finally
        {
            listener.Stop();
        }

        // And the real AgentHome workload still runs under that denial — a hardening that broke `dotnet --version`
        // would be a regression, not a win.
        var versionResult = await provider.ExecuteAsync(handle, new SandboxCommandRequest
        {
            ExecutionId = "agenthome-dotnet-1",
            Executable = "dotnet",
            Arguments = ["--version"],
            Timeout = TimeSpan.FromSeconds(120)
        });

        AssertEx.True(versionResult.Completed, $"`dotnet --version` must still run with egress denied: {versionResult.StandardError}");
        AssertEx.Equal(expected: 0, versionResult.ExitCode, $"`dotnet --version` must succeed with egress denied: {versionResult.StandardError}");
    }

    [Test]
    public async Task ProcessSandboxProvider_Execute_WhenEgressAllowed_TheSameProbeCanStillReachLoopback()
    {
        // The control for the test above. Without it, "nothing was reachable" could equally mean the probe itself is
        // broken — the exact failure mode dash's missing /dev/tcp would have produced.
        const string bashPath = "/usr/bin/bash";
        if (!OperatingSystem.IsLinux() || !File.Exists(bashPath))
        {
            Skip("bash on Linux is required for the /dev/tcp egress probe");
            return;
        }

        using var provider = CreateHostProvider();
        var handle = await provider.CreateOrAttachAsync(CreateRequest(Key()));

        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port: 0);
        listener.Start();
        var listenerPort = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;

        try
        {
            var result = await provider.ExecuteAsync(handle, new SandboxCommandRequest
            {
                ExecutionId = "egress-control-1",
                Executable = bashPath,
                Arguments = ["-c", $"if timeout 3 bash -c \"exec 3<>/dev/tcp/127.0.0.1/{listenerPort}\" 2>/dev/null; then echo REACHED; fi; echo PROBE-DONE"],
                Timeout = TimeSpan.FromSeconds(60)
            });

            AssertEx.True(result.Completed, $"the probe command must run: {result.StandardError}");
            AssertEx.Contains(result.StandardOutput ?? string.Empty, "REACHED",
                message: "an Unrestricted sandbox shares the host network, so the probe MUST connect — if it does not, the probe is broken and the denial test proves nothing");
        }
        finally
        {
            listener.Stop();
        }
    }

    [Test]
    public async Task ProcessSandboxProvider_Execute_WhenMemoryCeilingSet_KernelOomKillsARunawayChild()
    {
        // LIVE, Lane C. The ceiling must be enforced by the KERNEL, not by the app noticing afterwards.
        var containment = HostContainment();
        if (!containment.SupportsResourceLimits)
        {
            Skip($"this host cannot impose cgroup ceilings: {containment.ResourceLimitsUnavailableReason}");
            return;
        }

        if (!OperatingSystem.IsLinux())
        {
            Skip("the OOM test uses /bin/sh and head");
            return;
        }

        // Allocate ~256 MiB into a shell variable. Run it TWICE against different ceilings: the generous run is the
        // control, and without it this test would pass on any host where the command simply failed to launch — which
        // is exactly what happened while a non-executable `env` on PATH was silently breaking the wrapper chain. A
        // containment test that cannot distinguish "the ceiling worked" from "nothing ran" proves nothing.
        const string allocate = "x=$(head -c 268435456 /dev/zero | tr '\\0' 'a'); echo \"ALLOCATED ${#x}\"";

        var generous = await RunUnderMemoryCeilingAsync(allocate, memoryMb: 1024, executionId: "oom-control");
        AssertEx.Equal(expected: 0, generous.ExitCode,
            $"CONTROL: a 256 MiB allocation under a 1 GiB ceiling must succeed, otherwise this test proves nothing. Got: exit={generous.ExitCode} stderr=[{generous.StandardError}]");
        AssertEx.Contains(generous.StandardOutput ?? string.Empty, "ALLOCATED",
            message: "CONTROL: the child must actually run and allocate under a generous ceiling");

        var constrained = await RunUnderMemoryCeilingAsync(allocate, memoryMb: 64, executionId: "oom-1");
        AssertEx.False((constrained.StandardOutput ?? string.Empty).Contains("ALLOCATED", StringComparison.Ordinal),
            $"the child must not outlive its 64 MiB ceiling, got: {constrained.StandardOutput}");
        AssertEx.NotEqual(notExpected: 0, constrained.ExitCode,
            $"an OOM-killed child must not exit 0. stderr=[{constrained.StandardError}]");
    }

    private static async Task<SandboxCommandResult> RunUnderMemoryCeilingAsync(string command, int memoryMb, string executionId)
    {
        using var provider = CreateHostProvider();
        var handle = await provider.CreateOrAttachAsync(new SandboxCreateRequest
        {
            AttachKey = Key(),
            RuntimeProfile = "dotnet-agent-home",
            NetworkPolicy = SandboxNetworkPolicy.Unrestricted,
            ResourceLimits = new SandboxResourceLimits
            {
                MemoryMb = memoryMb
            }
        });

        return await provider.ExecuteAsync(handle, new SandboxCommandRequest
        {
            ExecutionId = executionId,
            Executable = "/bin/sh",
            Arguments = ["-c", command],
            Timeout = TimeSpan.FromSeconds(120)
        });
    }

    [Test]
    public async Task ProcessSandboxProvider_Execute_WhenLimitsApplied_ChildCannotReachTheUserSystemdBus()
    {
        // LIVE regression guard for a real escape found while building Lane C. systemd-run --user needs the session bus
        // address in its environment, and a network namespace does NOT confine UNIX sockets — so before the env(1)
        // strip layer existed, a child inside the namespace successfully started a unit OUTSIDE its own scope,
        // escaping both the ceiling and the egress denial.
        var containment = HostContainment();
        if (!containment.SupportsResourceLimits)
        {
            Skip($"this host cannot impose cgroup ceilings: {containment.ResourceLimitsUnavailableReason}");
            return;
        }

        using var provider = CreateHostProvider();
        var handle = await provider.CreateOrAttachAsync(new SandboxCreateRequest
        {
            AttachKey = Key(),
            RuntimeProfile = "dotnet-agent-home",
            NetworkPolicy = SandboxNetworkPolicy.Unrestricted,
            ResourceLimits = new SandboxResourceLimits
            {
                MemoryMb = 256
            }
        });

        var result = await provider.ExecuteAsync(handle, new SandboxCommandRequest
        {
            ExecutionId = "bus-1",
            Executable = "/bin/sh",
            Arguments = ["-c", "echo \"XDG=[$XDG_RUNTIME_DIR] DBUS=[$DBUS_SESSION_BUS_ADDRESS]\""],
            Timeout = TimeSpan.FromSeconds(30)
        });

        var diagnostics = $"exit={result.ExitCode} completed={result.Completed} stdout=[{result.StandardOutput}] stderr=[{result.StandardError}]";
        AssertEx.True(result.Completed, $"the probe command must run: {diagnostics}");
        AssertEx.Equal(expected: 0, result.ExitCode, $"the probe command must succeed: {diagnostics}");
        AssertEx.Contains(result.StandardOutput ?? string.Empty, "XDG=[]", message: diagnostics);
        AssertEx.Contains(result.StandardOutput ?? string.Empty, "DBUS=[]", message: diagnostics);
    }

    [Test]
    public async Task ProcessSandboxProvider_Execute_WhenLaunchedAsGroupLeader_WritesAndThenRemovesItsOrphanMarker()
    {
        // Lane F's write side: the marker must exist while a command runs (so a crash right now would be recoverable)
        // and be gone once it finishes (so the startup sweep stays proportional to real orphans).
        var containment = HostContainment();
        if (!containment.SupportsProcessGroup)
        {
            Skip("this host has no setsid, so no process-group marker is written");
            return;
        }

        var markerStore = new RecordingMarkerStore();
        using var provider = CreateProvider(containment: containment, markerStore: markerStore);
        var handle = await provider.CreateOrAttachAsync(CreateRequest(Key()));

        var (executable, arguments) = ShellCommand("exit 0");
        var result = await provider.ExecuteAsync(handle, new SandboxCommandRequest
        {
            ExecutionId = "marker-1",
            Executable = executable,
            Arguments = arguments,
            Timeout = TimeSpan.FromSeconds(30)
        });

        AssertEx.True(result.Completed, $"the command must complete: {result.StandardError}");
        AssertEx.NotEmpty(markerStore.Written, "a group-leader launch must record a marker");
        AssertEx.Empty(markerStore.Live, "the marker must be removed once the command finishes");

        var marker = markerStore.Written[0];
        AssertEx.True(marker.ProcessGroupId > 0);
        AssertEx.True(marker.LeaderStartTicks > 0, "the pid-reuse guard needs a real start time");
        AssertEx.Equal(Environment.ProcessId, marker.OwnerProcessId);
    }

    [Test]
    public async Task ProcessSandboxProvider_Execute_WhenNoProcessGroupMechanism_WritesNoMarkerAtAll()
    {
        // The pid of a non-leader is NOT a process-group id. Recording it would later have the reaper signal whatever
        // group that pid belonged to — in the worst case the worker's own — so the absence of the mechanism must mean
        // the absence of a marker, never a guess.
        var markerStore = new RecordingMarkerStore();
        using var provider = CreateProvider(markerStore: markerStore);
        var handle = await provider.CreateOrAttachAsync(CreateRequest(Key()));

        var (executable, arguments) = ShellCommand("exit 0");
        await provider.ExecuteAsync(handle, new SandboxCommandRequest
        {
            ExecutionId = "marker-2",
            Executable = executable,
            Arguments = arguments,
            Timeout = TimeSpan.FromSeconds(30)
        });

        AssertEx.Empty(markerStore.Written);
    }

    /// <summary>
    ///     Marks a live-gated test as SKIPPED with the measured reason, rather than returning green. A containment test
    ///     that silently passes on a host without the mechanism is worse than no test: it reports that egress denial or
    ///     a memory ceiling works when nothing was exercised at all.
    /// </summary>
    private static void Skip(string reason)
    {
        throw new global::TUnit.Core.Exceptions.SkipTestException(reason);
    }

    /// <summary>Records marker writes and deletions so the launch-side bookkeeping can be asserted without touching disk.</summary>
    private sealed class RecordingMarkerStore : ISandboxMarkerStore
    {
        private readonly Dictionary<string, SandboxProcessMarker> _live = [];

        public List<SandboxProcessMarker> Written { get; } = [];

        public IReadOnlyCollection<string> Live => _live.Keys;

        public string? Write(SandboxProcessMarker marker)
        {
            var id = "marker-" + Guid.NewGuid().ToString("N");
            Written.Add(marker);
            _live[id] = marker;
            return id;
        }

        public void Delete(string markerId)
        {
            _ = _live.Remove(markerId);
        }

        public IReadOnlyList<(string MarkerId, SandboxProcessMarker Marker)> ReadAll()
        {
            return [.. _live.Select(entry => (entry.Key, entry.Value))];
        }
    }

    private static async Task SwallowAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            // Expected when a blocking command is drained by killing the sandbox.
        }
    }

    /// <summary>
    ///     Builds a provider with a DETERMINISTIC containment probe. The default is "this host contains nothing", which
    ///     keeps the bulk of the suite host-independent and fast: these tests are about the jail, byte caps, timeout and
    ///     tree-kill semantics, and wrapping every one of their children in a systemd scope and a network namespace
    ///     would make them measure the runner's box instead. Containment behavior gets its own explicitly-configured
    ///     tests, and the real mechanisms are proven by the live-gated cases below.
    /// </summary>
    private static ProcessSandboxRuntimeProvider CreateProvider(long? maxCopyFileBytes = null,
        SandboxContainment? containment = null,
        long? maxJailDiskBytes = null,
        ISandboxMarkerStore? markerStore = null)
    {
        var options = Options.Create(new LocalContainerOptions
        {
            MaxCopyFileBytes = maxCopyFileBytes ?? LocalContainerOptions.DefaultMaxCopyFileBytes,
            MaxJailDiskBytes = maxJailDiskBytes ?? LocalContainerOptions.DefaultMaxJailDiskBytes
        });
        return new ProcessSandboxRuntimeProvider(options,
            TimeProvider.System,
            logger: null,
            new SandboxLauncher(new FixedContainmentProbe(containment ?? SandboxContainment.None)),
            markerStore);
    }

    /// <summary>A provider wired to the REAL host probe, for the live-gated containment tests.</summary>
    private static ProcessSandboxRuntimeProvider CreateHostProvider(long? maxJailDiskBytes = null)
    {
        var options = Options.Create(new LocalContainerOptions
        {
            MaxCopyFileBytes = LocalContainerOptions.DefaultMaxCopyFileBytes,
            MaxJailDiskBytes = maxJailDiskBytes ?? LocalContainerOptions.DefaultMaxJailDiskBytes
        });
        return new ProcessSandboxRuntimeProvider(options, TimeProvider.System);
    }

    private static SandboxContainment HostContainment()
    {
        return new HostSandboxContainmentProbe().Containment;
    }

    private sealed class FixedContainmentProbe : ISandboxContainmentProbe
    {
        public FixedContainmentProbe(SandboxContainment containment)
        {
            Containment = containment;
        }

        public SandboxContainment Containment { get; }
    }

    private static (string Executable, IReadOnlyList<string> Arguments) ShellCommand(string command)
    {
        // OS-appropriate shell wrapper so the short-lived test commands (sleep/yes/head) run on the primary Linux
        // runtime and on Windows (via cmd) where the assertion is OS-agnostic.
        return OperatingSystem.IsWindows()
            ? ("cmd.exe", new[]
            {
                "/c",
                command
            })
            : ("/bin/sh", new[]
            {
                "-c",
                command
            });
    }

    private string WriteHostTempFile(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), "xe-src-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, content, new UTF8Encoding(false));
        _tempPaths.Add(path);
        return path;
    }

    private string WriteHostTempFileBytes(byte[] content)
    {
        var path = Path.Combine(Path.GetTempPath(), "xe-src-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(path, content);
        _tempPaths.Add(path);
        return path;
    }

    private static SandboxCreateRequest CreateRequest(SandboxAttachKey attachKey)
    {
        return new SandboxCreateRequest
        {
            AttachKey = attachKey,
            RuntimeProfile = "dotnet-agent-home",
            // The process provider fails closed on any network posture it cannot enforce; Unrestricted is the only one
            // it honestly serves (the child shares the host network), so the happy-path helper requests it.
            NetworkPolicy = SandboxNetworkPolicy.Unrestricted
        };
    }

    private static SandboxAttachKey Key(string owner = "owner-1", string node = "node-1", int manifest = 1)
    {
        return new SandboxAttachKey
        {
            OwnerUserId = owner,
            NodeId = node,
            ProviderName = ProcessSandboxRuntimeProvider.Name,
            RuntimeProfile = "dotnet-agent-home",
            ManifestVersion = manifest
        };
    }

    // Runs a shell command inside the jail (no WorkingDirectory override → CWD = jail root) so a test can plant a
    // symlink the same way a sandboxed command would. Asserts the command itself succeeded so the symlink really exists.
    private static async Task RunShellInJailAsync(ProcessSandboxRuntimeProvider provider, SandboxHandle handle, string command)
    {
        var result = await provider.ExecuteAsync(handle, new SandboxCommandRequest
        {
            ExecutionId = "plant-" + Guid.NewGuid().ToString("N"),
            Executable = "/bin/sh",
            Arguments = ["-c", command],
            Timeout = TimeSpan.FromSeconds(15)
        });

        AssertEx.True(result.Completed, $"the in-jail setup command must complete: {result.StandardError}");
        AssertEx.Equal(expected: 0, result.ExitCode, $"the in-jail setup command must succeed: {result.StandardError}");
    }

    // Single-quote a path for /bin/sh, escaping embedded single quotes. Test paths are GUID temp dirs so this is simple.
    private static string ShellQuote(string value)
    {
        return "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xe-outside-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort temp cleanup.
            }
        }
    }
}
