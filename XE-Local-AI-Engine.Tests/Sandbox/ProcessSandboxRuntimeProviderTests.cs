namespace XE_Local_AI_Engine.Tests.Sandbox;

using System.Text;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;
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
        var developmentKey = agentHomeKey with { RuntimeProfile = "development-local", ManifestVersion = 2 };

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
        // v1 process model: no read-only mounts, no network isolation mechanism (decision D-1), and no resource-limit
        // enforcement — the CPU/mem/PID ceilings in SandboxResourceLimits are ignored (rlimit enforcement is a post-RC
        // follow-up), so the provider must not advertise SupportsResourceLimits.
        AssertEx.False(capabilities.HasFlag(SandboxProviderCapabilities.SupportsResourceLimits));
        AssertEx.False(capabilities.HasFlag(SandboxProviderCapabilities.SupportsReadOnlyMounts));
        AssertEx.False(capabilities.HasFlag(SandboxProviderCapabilities.SupportsNetworkPolicy));
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
        using var provider = CreateProvider();

        // None (no network) demands egress isolation the provider cannot enforce → fail closed.
        await AssertEx.ThrowsAsync<SandboxCapabilityNotSupportedException>(() => provider.CreateOrAttachAsync(new SandboxCreateRequest
        {
            AttachKey = Key(),
            RuntimeProfile = "dotnet-agent-home",
            NetworkPolicy = SandboxNetworkPolicy.None
        }));

        // Restricted (egress allow-list) is likewise unenforceable → fail closed.
        await AssertEx.ThrowsAsync<SandboxCapabilityNotSupportedException>(() => provider.CreateOrAttachAsync(new SandboxCreateRequest
        {
            AttachKey = Key(),
            RuntimeProfile = "dotnet-agent-home",
            NetworkPolicy = SandboxNetworkPolicy.Restricted
        }));
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

    private static ProcessSandboxRuntimeProvider CreateProvider(long? maxCopyFileBytes = null)
    {
        var options = Options.Create(new LocalContainerOptions
        {
            MaxCopyFileBytes = maxCopyFileBytes ?? LocalContainerOptions.DefaultMaxCopyFileBytes
        });
        return new ProcessSandboxRuntimeProvider(options, TimeProvider.System);
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
