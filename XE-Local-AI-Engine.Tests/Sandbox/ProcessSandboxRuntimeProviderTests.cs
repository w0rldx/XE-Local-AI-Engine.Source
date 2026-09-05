namespace XE_Local_AI_Engine.Tests.Sandbox;

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Options;
using TUnit.Core.Exceptions;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Reaping;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

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
        var (executable, arguments) = SleepCommand(30);
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

        var (executable, arguments) = SleepCommand(30);
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
        var (executable, arguments) = BulkOutputCommand();
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
    [RunOn(OS.Linux)]
    public async Task ProcessSandboxProvider_Execute_WhenWorkingDirectoryTraversesIntermediateSymlink_Rejects()
    {
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
    [RunOn(OS.Linux)]
    public async Task ProcessSandboxProvider_Execute_WhenWorkingDirectoryIsLeafSymlink_Rejects()
    {
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
    public async Task ProcessSandboxProvider_ResetDirectory_RemovesOnlyRequestedSubtree()
    {
        using var provider = CreateProvider();
        var handle = await provider.CreateOrAttachAsync(CreateRequest(Key()));
        var selected = WriteHostTempFile("old");
        var other = WriteHostTempFile("keep");
        await provider.CopyIntoAsync(handle, new SandboxCopyRequest
        {
            SourcePath = selected,
            DestinationPath = "/agent-home/workspace/selected/a.txt"
        });
        await provider.CopyIntoAsync(handle, new SandboxCopyRequest
        {
            SourcePath = other,
            DestinationPath = "/agent-home/memory/keep.txt"
        });

        await provider.ResetDirectoryAsync(handle, "/agent-home/workspace/selected");

        await AssertEx.ThrowsAsync<FileNotFoundException>(() =>
            provider.ReadFileAsync(handle, "/agent-home/workspace/selected/a.txt"));
        AssertEx.Equal("keep", await provider.ReadFileAsync(handle, "/agent-home/memory/keep.txt"));
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
    // The O_NOFOLLOW atomic refusal is the Linux guarantee under test; on other hosts the fallback open does
    // not refuse a symlink, so the assertion is Linux-only (the primary runtime).
    [RunOn(OS.Linux)]
    public async Task ProcessSandboxProvider_CopyInto_WhenFinalComponentIsSymlink_Rejects()
    {
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
    // Real symlink semantics + the no-follow open are the Linux guarantee under test.
    [RunOn(OS.Linux)]
    public async Task ProcessSandboxProvider_ReadFile_WhenPathTraversesJailSymlink_Rejects()
    {
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
    [RunOn(OS.Linux)]
    public async Task ProcessSandboxProvider_CopyOut_WhenJailSourceIsEscapingSymlink_Rejects()
    {
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
    [RunOn(OS.Linux)]
    public async Task ProcessSandboxProvider_CopyInto_WhenDestinationComponentIsEscapingSymlink_Rejects()
    {
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
    [RunOn(OS.Linux)]
    public async Task ProcessSandboxProvider_CopyInto_WhenDestinationComponentIsSymlink_DoesNotCreateOutsideDirectories()
    {
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
    public async Task ProcessSandboxProvider_CopyInto_WhenOverPerFileCap_ThrowsAndWritesNothing()
    {
        // The provider must fail the copy so AgentHome cleanup runs; returning success would let a snapshot claim bytes
        // that were never written.
        using var provider = CreateProvider(8);
        var handle = await provider.CreateOrAttachAsync(CreateRequest(Key()));
        var source = WriteHostTempFileBytes([1, 2, 3, 4, 5, 6, 7, 8, 9]); // 9 bytes = over the 8-byte cap

        await AssertEx.ThrowsAsync<InvalidDataException>(() => provider.CopyIntoAsync(handle, new SandboxCopyRequest
        {
            SourcePath = source,
            DestinationPath = "workspace/over.bin"
        }));

        // Nothing was written (never truncated to the stale size).
        await AssertEx.ThrowsAsync<FileNotFoundException>(() => provider.ReadFileAsync(handle, "workspace/over.bin"));
    }

    [Test]
    public async Task ProcessSandboxProvider_CopyInto_WhenSourceKeepsGrowing_ThrowsAndWritesNothing()
    {
        const int initialBytes = 4 * 1024;
        const int capBytes = 64 * 1024;
        using var provider = CreateProvider(capBytes);
        var handle = await provider.CreateOrAttachAsync(CreateRequest(Key()));
        var source = WriteHostTempFileBytes(new byte[initialBytes]);
        using var stop = new CancellationTokenSource();
        var sourceExceededCap = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = Task.Run(async () =>
        {
            var block = new byte[4096];
            await using var stream = new FileStream(source, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            while (stream.Length <= capBytes)
            {
                await stream.WriteAsync(block, stop.Token);
            }

            sourceExceededCap.SetResult();
            await Task.Delay(Timeout.Infinite, stop.Token);
        });

        try
        {
            // Establish the rejection precondition explicitly instead of racing the synchronous guarded read against
            // an ungated Task.Run. The writer parks once the source crosses the cap, keeping disk use bounded.
            await sourceExceededCap.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await AssertEx.ThrowsAsync<InvalidDataException>(() => provider.CopyIntoAsync(handle, new SandboxCopyRequest
            {
                SourcePath = source,
                DestinationPath = "workspace/growing.bin"
            }));
        }
        finally
        {
            await stop.CancelAsync();
            try
            {
                await writer;
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation releases the parked writer.
            }
        }

        await AssertEx.ThrowsAsync<FileNotFoundException>(() => provider.ReadFileAsync(handle, "workspace/growing.bin"));
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
        var (executable, arguments) = SleepCommand(30);
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

        var (executable, arguments) = SleepCommand(30);
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
    [RunOn(OS.Linux)]
    public async Task ProcessSandboxProvider_Execute_DoesNotLeakWorkerEnvironment_ButAllowlistedAndRequestVarsAppear()
    {
        // Linux-only: uses `printenv` and /bin/sh. Windows env behavior is covered by the same allow-list logic.
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

    /// <summary>
    ///     The Windows machine-wide configuration roots must survive the environment scrub.
    ///     <para>
    ///         NuGet.Common resolves the machine-wide NuGet configuration directory by reading these names directly and
    ///         combining the result into a path. With all of them scrubbed the combine receives null, and
    ///         <c>dotnet restore</c> fails inside every Development workspace on Windows with
    ///         "NuGet.targets(782,5): error : Value cannot be null. (Parameter 'path1')" — before it considers a single
    ///         package, so no amount of source or fallback-folder configuration can work around it.
    ///     </para>
    ///     <para>
    ///         Runs on Linux, where these names carry no meaning, precisely so the mechanism is proven where the suite
    ///         actually runs: the allow-list is OS-agnostic — it forwards whichever of its names exist in the parent —
    ///         so setting them here exercises the same code path Windows depends on. What this cannot prove is that
    ///         the list is COMPLETE for Windows; only a Windows run shows that.
    ///     </para>
    ///     <para>
    ///         <c>printenv</c> is executed DIRECTLY rather than through <c>/bin/sh -c</c>, and that detail is not
    ///         stylistic. <c>ProgramFiles(x86)</c> is not a valid shell identifier, and dash — which is <c>/bin/sh</c>
    ///         on Debian and Ubuntu — drops such names from the environment it passes on when it execs. Measured: the
    ///         variable is forwarded correctly by the provider and visible to a directly-executed child, while the
    ///         same probe behind <c>sh -c</c> reports it missing. Going through a shell would therefore fail this
    ///         test for a reason that has nothing to do with the allow-list, on the one name most likely to regress.
    ///     </para>
    /// </summary>
    [Test]
    [RunOn(OS.Linux)]
    public async Task ProcessSandboxProvider_Execute_ForwardsTheWindowsMachineWideConfigurationRoots()
    {
        var printenv = new[]
        {
            "/usr/bin/printenv",
            "/bin/printenv"
        }.FirstOrDefault(File.Exists);
        if (printenv is null)
        {
            Skip("This host has no printenv to read the child environment with.");
            return;
        }

        string[] names = ["ProgramData", "ProgramFiles", "ProgramFiles(x86)", "ALLUSERSPROFILE"];
        var expected = names.ToDictionary(name => name, name => $"/probe/{Guid.NewGuid():N}", StringComparer.Ordinal);

        foreach (var pair in expected)
        {
            Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }

        try
        {
            using var provider = CreateProvider();
            var handle = await provider.CreateOrAttachAsync(CreateRequest(Key()));

            var result = await provider.ExecuteAsync(handle, new SandboxCommandRequest
            {
                ExecutionId = "machine-wide-env-1",
                Executable = printenv,
                Arguments = [],
                Timeout = TimeSpan.FromSeconds(15)
            });

            AssertEx.True(result.Completed, $"printenv must complete: {result.StandardError}");
            AssertEx.Equal(expected: 0, result.ExitCode);

            var output = result.StandardOutput ?? string.Empty;
            foreach (var pair in expected)
            {
                AssertEx.True(output.Contains($"{pair.Key}={pair.Value}", StringComparison.Ordinal),
                    $"'{pair.Key}' must reach the sandbox child; without it NuGet combines a null machine-wide configuration path and dotnet restore fails on Windows");
            }
        }
        finally
        {
            foreach (var name in names)
            {
                Environment.SetEnvironmentVariable(name, value: null);
            }
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
    // The jail disk watchdog probe drives /bin/sh and dd.
    [RunOn(OS.Linux)]
    public async Task ProcessSandboxProvider_Execute_WhenChildExceedsJailDiskCap_TreeKillsAndReturnsIncomplete()
    {
        // Jail-dir disk watchdog: MaxCopyFileBytes bounds only the host→jail copy-in re-read. Without this watchdog a
        // command could fill the host disk from INSIDE the jail and nothing would stop it.
        // A 4 MiB ceiling against a command that writes far more, so the watchdog fires well inside the timeout.
        using var provider = CreateProvider(maxJailDiskBytes: 4L * 1024 * 1024);
        var handle = await provider.CreateOrAttachAsync(CreateRequest(Key()));

        var result = await provider.ExecuteAsync(handle, FillJailCommand("disk-1"));

        AssertEx.False(result.Completed, "a command that blows the jail disk ceiling must not be Completed");
        AssertEx.Equal(expected: -1, result.ExitCode);
        AssertEx.Contains(result.StandardError ?? string.Empty, "disk ceiling");
    }

    [Test]
    // The jail disk watchdog probe drives /bin/sh and dd.
    [RunOn(OS.Linux)]
    public async Task ProcessSandboxProvider_Execute_WhenTheSandboxAsksForATighterDiskCap_EnforcesTheSandboxCeiling()
    {
        // A caller whose workload writes almost nothing (the compute tool) should not have to inherit the node-wide
        // allowance sized for a workspace build. The per-sandbox ceiling is what lets it bound its own blast radius.
        using var provider = CreateProvider(maxJailDiskBytes: 512L * 1024 * 1024);
        var handle = await provider.CreateOrAttachAsync(CreateRequest(Key()) with
        {
            MaxJailDiskBytes = 4L * 1024 * 1024
        });

        var result = await provider.ExecuteAsync(handle, FillJailCommand("disk-tighten"));

        AssertEx.False(result.Completed, "the per-sandbox ceiling must terminate the command even though the node-wide one is far higher");
        AssertEx.Equal(expected: -1, result.ExitCode);
        // The reported number is the ceiling that actually fired, not the node's — a message naming 512 MiB after
        // stopping at 4 MiB would send an operator looking at the wrong setting.
        AssertEx.Contains(result.StandardError ?? string.Empty, (4L * 1024 * 1024).ToString(CultureInfo.InvariantCulture));
    }

    [Test]
    // The jail disk watchdog probe drives /bin/sh and dd.
    [RunOn(OS.Linux)]
    public async Task ProcessSandboxProvider_Execute_WhenTheSandboxAsksForALooserDiskCap_KeepsTheNodeCeiling()
    {
        // Tighten-only. The node-wide value is the OPERATOR's ceiling, so a create request that names a bigger number
        // must not widen it — otherwise the control is advisory and the caller sets its own limit.
        using var provider = CreateProvider(maxJailDiskBytes: 4L * 1024 * 1024);
        var handle = await provider.CreateOrAttachAsync(CreateRequest(Key()) with
        {
            MaxJailDiskBytes = 8L * 1024 * 1024 * 1024
        });

        var result = await provider.ExecuteAsync(handle, FillJailCommand("disk-loosen"));

        AssertEx.False(result.Completed, "a request asking for more than the node allows must still stop at the node's ceiling");
        AssertEx.Equal(expected: -1, result.ExitCode);
        AssertEx.Contains(result.StandardError ?? string.Empty, (4L * 1024 * 1024).ToString(CultureInfo.InvariantCulture));
    }

    [Test]
    // The jail disk watchdog probe drives /bin/sh and dd.
    [RunOn(OS.Linux)]
    public async Task ProcessSandboxProvider_Execute_WhenAnEarlierCommandAlreadyFilledTheJail_StopsTheNextOne()
    {
        // The ceiling bounds OCCUPANCY, not one command's growth. Re-baselining per command gave every command a fresh
        // allowance, so a caller could leave any amount of data in a jail by writing just under the line repeatedly and
        // no single command ever exceeded it. The second command here writes far LESS than the ceiling and must still
        // be stopped, because the jail it is writing into is already full.
        using var provider = CreateProvider(maxJailDiskBytes: 8L * 1024 * 1024);
        var handle = await provider.CreateOrAttachAsync(CreateRequest(Key()));

        var first = await provider.ExecuteAsync(handle, JailShellCommand("disk-occupancy-1", "dd if=/dev/zero of=fill-a.bin bs=1M count=6 2>/dev/null"));
        AssertEx.True(first.Completed, "the first command stays under the ceiling and must run normally");
        AssertEx.Equal(expected: 0, first.ExitCode);

        var second = await provider.ExecuteAsync(handle,
            JailShellCommand("disk-occupancy-2", "dd if=/dev/zero of=fill-b.bin bs=1M count=4 2>/dev/null; sleep 5"));

        AssertEx.False(second.Completed, "6 MiB already on disk plus 4 MiB more is past an 8 MiB ceiling, however little this command wrote");
        AssertEx.Equal(expected: -1, second.ExitCode);
        AssertEx.Contains(second.StandardError ?? string.Empty, "disk ceiling");
    }

    [Test]
    // The jail disk watchdog probe drives /bin/sh and dd.
    [RunOn(OS.Linux)]
    public async Task ProcessSandboxProvider_Execute_WhenTheFirstCommandWritesImmediately_DoesNotBankItsBytesInTheBaseline()
    {
        // The occupancy baseline is captured ONCE per sandbox and is permanent, so WHEN it is walked decides what the
        // ceiling can ever see. Walked after the child was launched, a command whose first act is a write had its own
        // bytes measured into the baseline — free for it and for every command after it — and a ceiling smaller than
        // that first write could never fire at all.
        //
        // The marker store is the seam that makes this deterministic instead of a race: the provider writes the marker
        // between launching the child and starting the disk watchdog, so a store that blocks there guarantees the
        // child's 8 MiB is on disk before any walk could happen. With the baseline anchored before the launch the
        // ceiling still fires; with it anchored after, this command runs to completion. The marker is only written for
        // a group-leader launch, so the real host containment is required for the seam to exist at all.
        var containment = HostContainment();
        if (!containment.SupportsProcessGroup)
        {
            Skip("this host has no setsid, so no marker is written and the ordering seam does not exist");
            return;
        }

        var markerStore = new BlockingMarkerStore(TimeSpan.FromSeconds(1.5));
        using var provider = CreateProvider(containment: containment, maxJailDiskBytes: 2L * 1024 * 1024, markerStore: markerStore);
        var handle = await provider.CreateOrAttachAsync(CreateRequest(Key()));

        // Writes 8 MiB as its very first act — four times the ceiling — then stays alive long enough for several
        // watchdog ticks.
        var result = await provider.ExecuteAsync(handle,
            JailShellCommand("disk-baseline-race", "dd if=/dev/zero of=first-write.bin bs=1M count=8 2>/dev/null; sleep 10"));

        AssertEx.False(result.Completed, "bytes a command writes before the baseline walk must count against its ceiling, not become part of the baseline");
        AssertEx.Equal(expected: -1, result.ExitCode);
        AssertEx.Contains(result.StandardError ?? string.Empty, "disk ceiling");
    }

    [Test]
    // The jail disk watchdog probe drives /bin/sh and dd.
    [RunOn(OS.Linux)]
    public async Task ProcessSandboxProvider_Execute_WhenTheNodeDisabledTheWatchdog_APerSandboxCeilingDoesNotReEnableIt()
    {
        // The node-wide value is the operator's, in both directions: a non-positive one turns the watchdog off, and a
        // per-sandbox request must not be able to switch it back on. min(node, request) gives that for free — the
        // asymmetry is worth a test of its own because the alternative reading (treat 0 as "no opinion") is tempting.
        using var provider = CreateProvider(maxJailDiskBytes: 0);
        var handle = await provider.CreateOrAttachAsync(CreateRequest(Key()) with
        {
            MaxJailDiskBytes = 1L * 1024 * 1024
        });

        // Well past the 1 MiB the sandbox asked for, and running long enough for several watchdog ticks to have fired.
        var result = await provider.ExecuteAsync(handle,
            JailShellCommand("disk-node-disabled", "dd if=/dev/zero of=fill.bin bs=1M count=8 2>/dev/null; sleep 5"));

        AssertEx.True(result.Completed, "a sandbox request must not re-enable a watchdog the operator turned off");
        AssertEx.Equal(expected: 0, result.ExitCode);
    }

    [Test]
    // The jail disk watchdog probe drives /bin/sh and dd.
    [RunOn(OS.Linux)]
    public async Task ProcessSandboxProvider_Execute_WhenAnAttachTightensTheCeilingMidCommand_TheRunningCommandKeepsItsSnapshot()
    {
        // Future-command tightening. The running command was launched against a budget and is judged by it to the end —
        // moving the line under a process that is mid-write would kill it for bytes that were within the rules when it
        // wrote them. The command that starts AFTER the attach gets the new, stricter ceiling, and is stopped at once
        // because the jail is already over it.
        using var provider = CreateProvider(maxJailDiskBytes: 512L * 1024 * 1024);
        var handle = await provider.CreateOrAttachAsync(CreateRequest(Key()) with
        {
            MaxJailDiskBytes = 32L * 1024 * 1024
        });

        var running = provider.ExecuteAsync(handle,
            JailShellCommand("disk-snapshot", "dd if=/dev/zero of=fill.bin bs=1M count=8 2>/dev/null; sleep 5"));

        // Long enough for the command to be well inside its run, short enough that several watchdog ticks still follow.
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        _ = await provider.CreateOrAttachAsync(CreateRequest(Key()) with
        {
            MaxJailDiskBytes = 1L * 1024 * 1024
        });

        var result = await running;
        AssertEx.True(result.Completed, "a command already running must be judged by the ceiling it started under");
        AssertEx.Equal(expected: 0, result.ExitCode);

        var next = await provider.ExecuteAsync(handle, JailShellCommand("disk-snapshot-next", "sleep 5"));
        AssertEx.False(next.Completed, "the tightened ceiling must apply to the next command, which starts in a jail already past it");
        AssertEx.Contains(next.StandardError ?? string.Empty, (1L * 1024 * 1024).ToString(CultureInfo.InvariantCulture));
    }

    [Test]
    public async Task ProcessSandboxProvider_Execute_WhenEgressDenied_ChildCannotReachLoopbackLanOrMetadata()
    {
        // LIVE default-deny egress. Asserts the mechanism's real effect, not the argument mapping — SandboxLaunchPlanTests covers
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
        using var listener = new TcpListener(IPAddress.Loopback, port: 0);
        listener.Start();
        var listenerPort = ((IPEndPoint)listener.LocalEndpoint).Port;

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
    [RunOn(OS.Linux)]
    public async Task ProcessSandboxProvider_AgentHomeShapedRun_ComposesCapabilityGatedDenialEndToEnd()
    {
        // The flip and the mechanism are two separate facts; this asserts they COMPOSE. It mirrors what
        // AgentHomeService.ResolveNetworkPolicy() does — pick None iff the provider advertises the capability — and
        // then proves a child of that sandbox really cannot reach loopback, the LAN, or the metadata endpoint.
        const string bashPath = "/usr/bin/bash";
        if (!File.Exists(bashPath))
        {
            Skip("bash is required for the /dev/tcp egress probe");
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

        using var listener = new TcpListener(IPAddress.Loopback, port: 0);
        listener.Start();
        var listenerPort = ((IPEndPoint)listener.LocalEndpoint).Port;

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
    [RunOn(OS.Linux)]
    public async Task ProcessSandboxProvider_Execute_WhenEgressAllowed_TheSameProbeCanStillReachLoopback()
    {
        // The control for the test above. Without it, "nothing was reachable" could equally mean the probe itself is
        // broken — the exact failure mode dash's missing /dev/tcp would have produced.
        const string bashPath = "/usr/bin/bash";
        if (!File.Exists(bashPath))
        {
            Skip("bash is required for the /dev/tcp egress probe");
        }

        using var provider = CreateHostProvider();
        var handle = await provider.CreateOrAttachAsync(CreateRequest(Key()));

        using var listener = new TcpListener(IPAddress.Loopback, port: 0);
        listener.Start();
        var listenerPort = ((IPEndPoint)listener.LocalEndpoint).Port;

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
    // The OOM probe drives /bin/sh and head.
    [RunOn(OS.Linux)]
    public async Task ProcessSandboxProvider_Execute_WhenMemoryCeilingSet_KernelOomKillsARunawayChild()
    {
        // LIVE cgroup resource ceiling. The ceiling must be enforced by the KERNEL, not by the app noticing afterwards.
        var containment = HostContainment();
        if (!containment.SupportsResourceLimits)
        {
            Skip($"this host cannot impose cgroup ceilings: {containment.ResourceLimitsUnavailableReason}");
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
        // LIVE regression guard for a real sandbox escape found while building the cgroup resource ceilings.
        // systemd-run --user needs the session bus
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
        // The orphan reaper's marker-store write side: the marker must exist while a command runs (so a crash right now would be recoverable)
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

        // The child must still be ALIVE when the marker is written. WriteProcessMarker reads /proc/<pid>/stat for the
        // pid-reuse guard and — correctly — records nothing when the process has already gone, so a command that exits
        // immediately makes this assertion a race against the launch: it passes on a loaded box and fails on an idle
        // one. The product behaviour under test is "a group-leader launch records a marker while it runs", so the
        // command has to run.
        var (executable, arguments) = SleepCommand(seconds: 1);
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

        // The LAST recorded state: an isolated launch pre-registers a pending marker first, and it is the completed
        // one that has to carry the pid the reaper signals with.
        var marker = markerStore.Written[^1];
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
        throw new SkipTestException(reason);
    }

    /// <summary>
    ///     A marker store that blocks while the provider COMPLETES a marker — the call it makes between launching the
    ///     child and starting the disk watchdog — so this stalls that window on purpose: it makes "the child got to
    ///     write before the engine measured the jail" a certainty rather than a race, without a test-only seam in the
    ///     provider.
    ///     <para>
    ///         The pre-registration that happens BEFORE an isolated launch returns immediately: blocking there would
    ///         only delay the child's start, which is not the window this test is about.
    ///     </para>
    /// </summary>
    private sealed class BlockingMarkerStore(TimeSpan delay) : ISandboxMarkerStore
    {
        public string? Write(SandboxProcessMarker marker)
        {
            if (marker.ProcessGroupId is not null)
            {
                Block();
            }

            return "marker-" + Guid.NewGuid().ToString("N");
        }

        public void Update(string markerId, SandboxProcessMarker marker)
        {
            Block();
        }

        public void Delete(string markerId)
        {
        }

        public IReadOnlyList<SandboxMarkerEntry> ReadAll()
        {
            return [];
        }

        private void Block()
        {
            // A never-set gate waited on with a timeout: the same block a sleep would give, through an API the repo's
            // analyzer wall allows.
            using var gate = new ManualResetEventSlim(initialState: false);
            _ = gate.Wait(delay);
        }
    }

    /// <summary>
    ///     Records marker writes, completions and deletions so the launch-side bookkeeping can be asserted without
    ///     touching disk. <see cref="Written" /> holds every recorded STATE in order, so an isolated launch shows its
    ///     pending registration followed by the completed marker.
    /// </summary>
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

        public void Update(string markerId, SandboxProcessMarker marker)
        {
            Written.Add(marker);
            _live[markerId] = marker;
        }

        public void Delete(string markerId)
        {
            _ = _live.Remove(markerId);
        }

        public IReadOnlyList<SandboxMarkerEntry> ReadAll()
        {
            return [.. _live.Select(entry => new SandboxMarkerEntry(entry.Key, entry.Value))];
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
    /// <summary>
    ///     The listing survey, which replaced a <c>find</c> shell-out that did not exist on Windows at all. Seeded
    ///     through <c>CopyIntoAsync</c> rather than a shell so the assertion itself is OS-independent — this is the
    ///     code path a Windows tester will run, and it has to be provable here.
    /// </summary>
    [Test]
    public async Task ProcessSandboxProvider_ListFiles_ReturnsJailRelativeEntries()
    {
        using var provider = CreateProvider();
        var handle = await provider.CreateOrAttachAsync(CreateRequest(Key()));
        await SeedAsync(provider, handle, "workspace/src/Program.cs", "code");
        await SeedAsync(provider, handle, "workspace/notes.md", "notes");
        await SeedAsync(provider, handle, "workspace/deep/nested/Widget.cs", "widget");

        var entries = await provider.ListFilesAsync(handle, new SandboxListFilesRequest
        {
            DirectoryPath = "/workspace",
            MaxEntries = 50
        });

        AssertEx.Contains(entries, "./notes.md");
        AssertEx.Contains(entries, "./src/Program.cs");
        AssertEx.Contains(entries, "./deep/nested/Widget.cs");

        var scoped = await provider.ListFilesAsync(handle, new SandboxListFilesRequest
        {
            DirectoryPath = "/workspace/src",
            MaxEntries = 50,
            NameGlob = "*.cs"
        });

        AssertEx.Equal(1, scoped.Count);
        AssertEx.Equal("./Program.cs", scoped[0]);
    }

    /// <summary>
    ///     The survey is a provider operation precisely so the jail controls apply to it. A directory argument that
    ///     escapes must be refused by the same lexical check a read is refused by — the shell-out put this
    ///     responsibility in an argument vector the caller had to compose correctly.
    /// </summary>
    [Test]
    public async Task ProcessSandboxProvider_ListFiles_WhenPathEscapesJail_Rejects()
    {
        using var provider = CreateProvider();
        var handle = await provider.CreateOrAttachAsync(CreateRequest(Key()));

        await AssertEx.ThrowsAsync<UnauthorizedAccessException>(() => provider.ListFilesAsync(handle, new SandboxListFilesRequest
        {
            DirectoryPath = "/../../etc",
            MaxEntries = 50
        }));

        await AssertEx.ThrowsAsync<UnauthorizedAccessException>(() => provider.SearchTextAsync(handle, new SandboxSearchTextRequest
        {
            DirectoryPath = "/../../etc",
            Pattern = "root",
            MaxMatches = 10,
            MaxOutputBytes = 4096
        }));
    }

    [Test]
    public async Task ProcessSandboxProvider_ListFiles_WhenDirectoryIsMissing_ThrowsRatherThanReturningEmpty()
    {
        using var provider = CreateProvider();
        var handle = await provider.CreateOrAttachAsync(CreateRequest(Key()));

        // An empty listing would read as "the workspace has no files", which is a different and misleading answer.
        await AssertEx.ThrowsAsync<DirectoryNotFoundException>(() => provider.ListFilesAsync(handle, new SandboxListFilesRequest
        {
            DirectoryPath = "/workspace/nope",
            MaxEntries = 50
        }));
    }

    [Test]
    public async Task ProcessSandboxProvider_SearchText_ReturnsPathLineAndText()
    {
        using var provider = CreateProvider();
        var handle = await provider.CreateOrAttachAsync(CreateRequest(Key()));
        await SeedAsync(provider, handle, "workspace/src/a.txt", "alpha\nneedle here\ngamma\n");
        await SeedAsync(provider, handle, "workspace/src/b.txt", "nothing\n");

        var matches = await provider.SearchTextAsync(handle, new SandboxSearchTextRequest
        {
            DirectoryPath = "/workspace",
            Pattern = "needle",
            MaxMatches = 10,
            MaxOutputBytes = 4096
        });

        AssertEx.Equal(1, matches.Count);
        AssertEx.Equal("./src/a.txt:2:needle here", matches[0]);
    }

    /// <summary>
    ///     A command running in the jail can plant a symlink out of it, and the lexical jail check passes such a path —
    ///     so the no-symlink walk is what stops the survey from enumerating a directory outside the workspace. Planted
    ///     the way the sibling read/copy tests plant one, which is why this is Linux-gated: the payload is a shell
    ///     command, not the assertion.
    /// </summary>
    [Test]
    [RunOn(OS.Linux)]
    public async Task ProcessSandboxProvider_ListFiles_WhenPathTraversesJailSymlink_Rejects()
    {
        using var provider = CreateProvider();
        var handle = await provider.CreateOrAttachAsync(CreateRequest(Key()));
        using var outside = new TempDir();
        await File.WriteAllTextAsync(Path.Combine(outside.Path, "secret.txt"), "OUTSIDE-THE-JAIL");

        await SeedAsync(provider, handle, "workspace/keep.txt", "x");
        await RunShellInJailAsync(provider, handle, "ln -s " + ShellQuote(outside.Path) + " workspace/escape");

        await AssertEx.ThrowsAsync<UnauthorizedAccessException>(() => provider.ListFilesAsync(handle, new SandboxListFilesRequest
        {
            DirectoryPath = "/workspace/escape",
            MaxEntries = 50
        }));

        // And a survey of the parent must not follow it either.
        var entries = await provider.ListFilesAsync(handle, new SandboxListFilesRequest
        {
            DirectoryPath = "/workspace",
            MaxEntries = 50
        });
        AssertEx.False(entries.Any(entry => entry.Contains("secret.txt", StringComparison.Ordinal)),
            "the survey must not enumerate through a planted symlink");
    }

    private async Task SeedAsync(ProcessSandboxRuntimeProvider provider, SandboxHandle handle, string sandboxPath, string content)
    {
        await provider.CopyIntoAsync(handle, new SandboxCopyRequest
        {
            SourcePath = WriteHostTempFile(content),
            DestinationPath = sandboxPath
        });
    }

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
        // OS-appropriate shell WRAPPER only. It does not make the command itself portable — see SleepCommand and
        // BulkOutputCommand for the cases where the command text had to diverge too.
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

    /// <summary>
    ///     A command that blocks for roughly <paramref name="seconds" /> and does so as a real parent/child process
    ///     pair, so the tree-kill cases have a descendant to reap.
    ///     <para>
    ///         Windows needs its own command, not just its own shell. <c>sleep</c> is not a cmd builtin and there is no
    ///         <c>sleep.exe</c> on stock Windows 11, so <c>cmd /c sleep 30</c> exits ~instantly with
    ///         <c>'sleep' is not recognized</c>. That silently inverted three assertions here: the timeout case saw a
    ///         command that had already completed, the cancel case had nothing left in flight to cancel, and the
    ///         sandbox-kill case passed for the wrong reason. <c>ping -n</c> is the portable stand-in — it is present on
    ///         every Windows install, spaces its probes one second apart, and unlike <c>timeout /t</c> it does not
    ///         refuse to run when standard input is redirected, which it always is here.
    ///     </para>
    /// </summary>
    private static (string Executable, IReadOnlyList<string> Arguments) SleepCommand(int seconds)
    {
        if (!OperatingSystem.IsWindows())
        {
            return ShellCommand($"sleep {seconds}");
        }

        // Passed as separate argv elements rather than one cmd command string: cmd's quote-stripping rules for
        // /c "..." are surprising, and there is nothing here that needs a shell to interpret it.
        return ("cmd.exe", new[]
        {
            "/c",
            "ping",
            "-n",
            (seconds + 1).ToString(CultureInfo.InvariantCulture),
            "127.0.0.1"
        });
    }

    /// <summary>
    ///     A command that writes far more than the captured-output cap to stdout, as multibyte lines.
    ///     <para>
    ///         The Linux form keeps the <c>yes | head</c> pipeline. Windows has neither tool, and building the same
    ///         volume from a cmd <c>for /L</c> loop takes minutes, so there the bytes are prepared as a file up front
    ///         and streamed with <c>type</c> — same shape reaching the pump (many multibyte lines, well past the cap),
    ///         at no measurable cost. The path is absolute so this does not depend on where the jail put the working
    ///         directory.
    ///     </para>
    /// </summary>
    private (string Executable, IReadOnlyList<string> Arguments) BulkOutputCommand()
    {
        const string line = "αααααααααααααααααααααααααααααααααααααααα";

        if (!OperatingSystem.IsWindows())
        {
            return ShellCommand($"yes '{line}' | head -n 200000");
        }

        // 100k lines x 40 two-byte characters + CRLF is ~8.2 MB — comfortably past the 4 MB cap the test asserts.
        var builder = new StringBuilder(100_000 * (line.Length + 1));
        for (var i = 0; i < 100_000; i++)
        {
            builder.AppendLine(line);
        }

        var path = Path.Combine(Path.GetTempPath(), "xe-bulk-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        _tempPaths.Add(path);

        return ("cmd.exe", new[]
        {
            "/c",
            "type",
            path
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

    /// <summary>
    ///     A command that grows the jail steadily rather than in one burst, so the periodic watchdog observes the
    ///     growth while the command is still running.
    /// </summary>
    private static SandboxCommandRequest FillJailCommand(string executionId)
    {
        return JailShellCommand(executionId,
            "i=0; while [ $i -lt 400 ]; do dd if=/dev/zero of=fill-$i.bin bs=1M count=2 2>/dev/null; i=$((i+1)); sleep 0.05; done");
    }

    /// <summary>
    ///     Runs a shell script with the jail as its working directory, under a timeout generous enough that a test
    ///     asserting on the DISK ceiling can never be reading a timeout kill by mistake.
    /// </summary>
    private static SandboxCommandRequest JailShellCommand(string executionId, string script)
    {
        return new SandboxCommandRequest
        {
            ExecutionId = executionId,
            Executable = "/bin/sh",
            Arguments = ["-c", script],
            Timeout = TimeSpan.FromSeconds(60)
        };
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
