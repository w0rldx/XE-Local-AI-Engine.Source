namespace XE_Local_AI_Engine.Tests.Sandbox;

using System.Globalization;
using Microsoft.Extensions.Options;
using TUnit.Core.Exceptions;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Isolation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <para>
///         LIVE coverage of the filesystem-isolated launch mode: the real trusted-binary resolution, the real
///         descriptor opens, the real chain, the real transient scope. These are the tests that can fail when the
///         boundary is broken, which the rendering tests by construction cannot.
///     </para>
///     <para>
///         <b>Opt-in, and they SKIP rather than pass.</b> Each one spawns real processes and creates real systemd user
///         scopes, so they are gated on <c>XE_COMPUTE_LIVE=1</c> and on the host actually being able to isolate. A
///         containment test that goes green on a box which contains nothing reports a guarantee that nothing
///         exercised — worse than no test at all.
///     </para>
/// </summary>
public sealed class SandboxIsolationLiveTests
{
    private const string EnabledVariable = "XE_COMPUTE_LIVE";

    // Long enough for a cold chain (a dbus round trip plus a dozen mounts), short enough that a hang is a failure.
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    [Test]
    public async Task Probe_MeasuresTheFilesystemBoundary_AsAvailableOnThisHost()
    {
        RequireOptIn();
        var containment = new HostSandboxContainmentProbe().Containment;

        if (TrustedBinaryResolver.Resolve("bwrap") is null)
        {
            Skip("this host has no root-owned bwrap under /usr/bin, /bin or /usr/local/bin.");
        }

        // The probe RUNS the production chain and checks fifteen positive and negative controls before it says yes,
        // so this assertion carries all of them: canary invisibility, pid 2, EROFS on /dev, an empty /run, no bus
        // socket, and a loopback connect that fails inside while succeeding outside.
        AssertEx.Null(containment.FilesystemIsolationUnavailableReason);
        AssertEx.True(containment.SupportsFilesystemIsolation);

        await Task.CompletedTask;
    }

    [Test]
    public async Task IsolatedCommand_CannotSeeTheHostFilesystem_ButCanWriteItsOwnJail()
    {
        RequireIsolationCapableHost();
        using var provider = CreateProvider();
        var handle = await CreateIsolatedSandboxAsync(provider);

        var canary = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), $".xe-isolation-live-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(canary, "host");
        try
        {
            var result = await RunShellAsync(provider,
                handle,
                $"""
                 if [ -e '{canary}' ]; then echo CANARY=PRESENT; else echo CANARY=ABSENT; fi
                 if [ -e '{handle.WorkingRoot}' ]; then echo JAILPATH=PRESENT; else echo JAILPATH=ABSENT; fi
                 echo CWD=$(pwd)
                 echo ok > marker.txt && echo WROTE=OK
                 if touch /usr/xe-live 2>/dev/null; then echo USR=WRITABLE; else echo USR=READONLY; fi
                 """);

            AssertEx.Equal(expected: 0, result.ExitCode, result.StandardError);
            AssertEx.Contains(result.StandardOutput, "CANARY=ABSENT");
            // The jail is never exposed at its host pathname either: inside, it is /work and nothing else.
            AssertEx.Contains(result.StandardOutput, "JAILPATH=ABSENT");
            AssertEx.Contains(result.StandardOutput, "CWD=/work");
            AssertEx.Contains(result.StandardOutput, "WROTE=OK");
            AssertEx.Contains(result.StandardOutput, "USR=READONLY");

            // The host side of the descriptor proof: what the sandbox wrote to /work landed in the engine's jail, so
            // the --bind-fd really did survive setsid → systemd-run → bwrap.
            AssertEx.True(File.Exists(Path.Combine(handle.WorkingRoot!, "marker.txt")),
                "the descriptor-bound jail must be the engine's own directory");
        }
        finally
        {
            File.Delete(canary);
        }

        await Task.CompletedTask;
    }

    [Test]
    public async Task IsolatedCommand_ReadsANamedTree_AndCannotWriteIt()
    {
        RequireIsolationCapableHost();
        using var tree = new EngineOwnedTree();
        using var provider = CreateProvider();
        var handle = await CreateIsolatedSandboxAsync(provider, readOnlyTrees: [tree.Path]);

        var result = await RunShellAsync(provider,
            handle,
            $"""
             cat '{tree.Path}/payload.txt'
             if touch '{tree.Path}/planted.txt' 2>/dev/null; then echo TREE=WRITABLE; else echo TREE=READONLY; fi
             """);

        AssertEx.Equal(expected: 0, result.ExitCode, result.StandardError);
        // Bound at its own canonical path, so an interpreter with absolute paths compiled into it still resolves.
        AssertEx.Contains(result.StandardOutput, "payload");
        AssertEx.Contains(result.StandardOutput, "TREE=READONLY");
        AssertEx.False(File.Exists(Path.Combine(tree.Path, "planted.txt")), "a read-only tree must not gain files");

        await Task.CompletedTask;
    }

    [Test]
    public async Task IsolatedCommand_FindsEveryReadOnlySurfaceReadOnly_AndEveryDeviceStillUsable()
    {
        // Asserted here as well as inside the capability probe, and deliberately so: the probe's version of these
        // controls decides whether the capability is advertised, so a regression in the chain would withdraw the
        // capability and this suite would skip. Asserting them again from outside means a broken --remount-ro shows
        // up as a failing test rather than as a quiet absence of tests.
        RequireIsolationCapableHost();
        using var provider = CreateProvider();
        var handle = await CreateIsolatedSandboxAsync(provider);

        var result = await RunShellAsync(provider,
            handle,
            """
            if touch / 2>/dev/null && touch /xe 2>/dev/null; then echo ROOT=WRITABLE; else echo ROOT=READONLY; fi
            DEVERR=$(touch /dev/xe 2>&1); if [ -e /dev/xe ]; then echo DEV=WRITABLE; else case "$DEVERR" in *"Read-only"*) echo DEV=EROFS;; *) echo DEV=REFUSED;; esac; fi
            if touch /proc/xe 2>/dev/null; then echo PROC=WRITABLE; else echo PROC=REFUSED; fi
            if touch /etc/xe 2>/dev/null; then echo ETC=WRITABLE; else echo ETC=READONLY; fi
            if echo x > /dev/null 2>/dev/null; then echo DEVNULL=OK; else echo DEVNULL=BROKEN; fi
            echo URANDOM=$(head -c 4 /dev/urandom | wc -c)
            echo PID=$$
            echo PASSWD=$(grep -c . /etc/passwd)
            echo RUNENTRIES=$(ls -A /run | wc -l)
            """);

        AssertEx.Equal(expected: 0, result.ExitCode, result.StandardError);
        AssertEx.Contains(result.StandardOutput, "ROOT=READONLY");
        // EROFS specifically: that is what a remounted-read-only mount answers, and it distinguishes "the remount
        // worked" from "the path happened not to exist".
        AssertEx.Contains(result.StandardOutput, "DEV=EROFS");
        AssertEx.Contains(result.StandardOutput, "PROC=REFUSED");
        AssertEx.Contains(result.StandardOutput, "ETC=READONLY");
        // The positive half: a read-only /dev that broke /dev/null would be a worse bug than the one being prevented.
        AssertEx.Contains(result.StandardOutput, "DEVNULL=OK");
        AssertEx.Contains(result.StandardOutput, "URANDOM=4");
        AssertEx.Contains(result.StandardOutput, "PID=2");
        AssertEx.Contains(result.StandardOutput, "PASSWD=2");
        AssertEx.Contains(result.StandardOutput, "RUNENTRIES=0");

        await Task.CompletedTask;
    }

    [Test]
    public async Task IsolatedCommand_PropagatesANonZeroExitCode_ThroughTheInnerPidOne()
    {
        RequireIsolationCapableHost();
        using var provider = CreateProvider();
        var handle = await CreateIsolatedSandboxAsync(provider);

        // bwrap is pid 1 inside the namespace and the workload is pid 2, so the exit code crosses two process
        // boundaries plus systemd-run and setsid before it reaches here. If any layer swallowed it, every failing
        // command in the sandbox would look successful.
        var result = await RunShellAsync(provider, handle, "exit 42");

        AssertEx.Equal(expected: 42, result.ExitCode);
        AssertEx.True(result.Completed);

        await Task.CompletedTask;
    }

    [Test]
    public async Task IsolatedCommand_ReportsASignalDeath_AsANonZeroExit()
    {
        RequireIsolationCapableHost();
        using var provider = CreateProvider();
        var handle = await CreateIsolatedSandboxAsync(provider);

        var result = await RunShellAsync(provider, handle, "kill -9 $$");

        AssertEx.NotEqual(notExpected: 0, result.ExitCode);

        await Task.CompletedTask;
    }

    [Test]
    public async Task Termination_AtTheTimeout_KillsEvenADetachedGrandchild()
    {
        RequireIsolationCapableHost();
        using var provider = CreateProvider();
        var handle = await CreateIsolatedSandboxAsync(provider);

        // A grandchild that left the process group on purpose. This is the case a tree-kill and a kill(-pgid) both
        // miss, and the only reason the transient scope's cgroup is the kill authority: it is the one container
        // nothing inside can leave.
        var result = await RunTimingOutShellAsync(provider,
            handle,
            """
            setsid sh -c 'echo $$ > /work/grandchild.pid; exec sleep 300' < /dev/null > /dev/null 2>&1 &
            sleep 1
            while true; do sleep 1; done
            """);

        AssertEx.False(result.Completed, "a non-terminating command must be stopped at the timeout");

        // The pid is the one the sandbox saw, which is its PID-namespace pid — meaningless on the host. What IS
        // observable from here is the scope: once its cgroup is empty the unit is collected, so a unit still loaded
        // means something survived the kill.
        AssertEx.True(File.Exists(Path.Combine(handle.WorkingRoot!, "grandchild.pid")),
            "the detached grandchild must actually have started, or this test proves nothing");
        await AssertEx.EventuallyAsync(() => LoadedEngineScopeCount() == 0,
            TimeSpan.FromSeconds(20),
            "every transient sandbox scope must be gone after the command was terminated");

        await Task.CompletedTask;
    }

    [Test]
    public async Task Termination_OnSandboxKill_EmptiesTheScope()
    {
        RequireIsolationCapableHost();
        var provider = CreateProvider();
        try
        {
            var handle = await CreateIsolatedSandboxAsync(provider);

            var running = RunTimingOutShellAsync(provider, handle, "while true; do sleep 1; done");
            await AssertEx.EventuallyAsync(() => LoadedEngineScopeCount() > 0, TimeSpan.FromSeconds(20), "the command's scope must appear");

            await provider.KillAsync(handle);

            await AssertEx.EventuallyAsync(() => LoadedEngineScopeCount() == 0,
                TimeSpan.FromSeconds(20),
                "a sandbox kill must empty the transient scope, not only tree-kill the outer helper");

            // Awaited before the provider is disposed: the in-flight command owns the process, and disposing the
            // provider under it would make the assertions above race a teardown they were not measuring.
            _ = await running;
        }
        finally
        {
            provider.Dispose();
        }
    }

    // ---- helpers ----

    private static int LoadedEngineScopeCount()
    {
        var isolation = new HostSandboxContainmentProbe().Containment.FilesystemIsolation;

        return SandboxScopeUnitKiller.TryCreate(isolation)?.ListEngineOwnedUnits().Count ?? 0;
    }

    private static async Task<SandboxHandle> CreateIsolatedSandboxAsync(ProcessSandboxRuntimeProvider provider,
        IReadOnlyList<string>? readOnlyTrees = null)
    {
        return await provider.CreateOrAttachAsync(new SandboxCreateRequest
        {
            AttachKey = new SandboxAttachKey
            {
                OwnerUserId = "owner",
                NodeId = $"node-{Guid.NewGuid():N}",
                ProviderName = ProcessSandboxRuntimeProvider.Name,
                RuntimeProfile = "compute",
                ManifestVersion = 1
            },
            RuntimeProfile = "compute",
            Isolation = SandboxIsolationMode.Filesystem,
            ReadOnlyTrees = readOnlyTrees,
            ThreadLimit = 1,
            ResourceLimits = new SandboxResourceLimits { MemoryMb = 1024, PidsLimit = 64, CpuCount = 2 }
        });
    }

    private static Task<SandboxCommandResult> RunShellAsync(ProcessSandboxRuntimeProvider provider, SandboxHandle handle, string script)
    {
        return provider.ExecuteAsync(handle, ShellRequest(script, CommandTimeout));
    }

    private static Task<SandboxCommandResult> RunTimingOutShellAsync(ProcessSandboxRuntimeProvider provider, SandboxHandle handle, string script)
    {
        return provider.ExecuteAsync(handle, ShellRequest(script, TimeSpan.FromSeconds(6)));
    }

    private static SandboxCommandRequest ShellRequest(string script, TimeSpan timeout)
    {
        // The shell comes from the read-only /usr bind. /bin/sh rather than bash or python3: the generic isolated mode
        // must be provable without a language runtime being installed inside, and coreutils is all it has.
        return new SandboxCommandRequest
        {
            ExecutionId = Guid.NewGuid().ToString("N"),
            Executable = "/bin/sh",
            Arguments = ["-c", script],
            Timeout = timeout
        };
    }

    private static ProcessSandboxRuntimeProvider CreateProvider()
    {
        return new ProcessSandboxRuntimeProvider(Options.Create(new LocalContainerOptions
            {
                MaxCopyFileBytes = LocalContainerOptions.DefaultMaxCopyFileBytes,
                MaxJailDiskBytes = LocalContainerOptions.DefaultMaxJailDiskBytes
            }),
            TimeProvider.System);
    }

    /// <summary>
    ///     Skips only when the host genuinely lacks the mechanism, and FAILS when it has it but the boundary does not
    ///     hold.
    ///     <para>
    ///         The distinction is load-bearing. A gate that skips on any probe failure would turn every regression in
    ///         the chain into a silent green run: break <c>--remount-ro /dev</c>, the probe's control fails, the
    ///         capability goes false, and the whole suite politely skips the tests that would have caught it. Once a
    ///         root-owned <c>bwrap</c> and a user bus are present, this host is expected to isolate, and anything else
    ///         is a failure.
    ///     </para>
    /// </summary>
    private static void RequireIsolationCapableHost()
    {
        RequireOptIn();

        if (TrustedBinaryResolver.Resolve("bwrap") is null)
        {
            Skip("this host has no root-owned bwrap under /usr/bin, /bin or /usr/local/bin.");
        }

        var containment = new HostSandboxContainmentProbe().Containment;
        if (!containment.SupportsFilesystemIsolation)
        {
            AssertEx.True(condition: false,
                $"this host has a trusted bwrap, so the filesystem boundary must hold; the probe reported: {containment.FilesystemIsolationUnavailableReason}");
        }
    }

    private static void RequireOptIn()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip("the isolated launch chain is Linux-only.");
        }

        if (!string.Equals(Environment.GetEnvironmentVariable(EnabledVariable), "1", StringComparison.Ordinal))
        {
            Skip($"set {EnabledVariable}=1 to allow this suite to create real systemd user scopes and mount namespaces.");
        }
    }

    private static void Skip(string reason)
    {
        throw new SkipTestException(reason);
    }

    /// <summary>A short-lived engine-owned directory standing in for a provisioned interpreter tree.</summary>
    private sealed class EngineOwnedTree : IDisposable
    {
        public EngineOwnedTree()
        {
            // Under the engine user's home, not under /tmp: /tmp inside an isolated sandbox is the jail's own temp
            // directory, so a tree bound from there would be shadowed by it — which is why the chain rejects it.
            Path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                string.Create(CultureInfo.InvariantCulture, $".xe-live-tree-{Guid.NewGuid():N}"));
            Directory.CreateDirectory(Path);
            File.WriteAllText(System.IO.Path.Combine(Path, "payload.txt"), "payload");
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort test cleanup.
            }
        }
    }
}
