namespace XE_Local_AI_Engine.Tests.Sandbox;

using System.Diagnostics;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Isolation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The CONTRACT half of the filesystem boundary, asserted without starting anything: that the capability is
///     independent of the others, that asking for it on a host without it is refused rather than downgraded, and —
///     the assertion that protects every existing caller — that a request which does not mention it produces exactly
///     the chain it produced before this mode existed.
/// </summary>
public sealed class SandboxFilesystemIsolationContractTests
{
    [Test]
    public async Task ContainmentProbe_WhenTheFilesystemProbeThrows_KeepsTheResourceAndNetworkResults()
    {
        // The reason the filesystem probe is caught separately. It does far more than the other two — descriptors,
        // memory files, five namespaces, a shell script — so it has far more ways to fail, and a shared guard would
        // let one of those failures silently withdraw ceilings and egress denial that were measured successfully.
        var reference = new HostSandboxContainmentProbe().Containment;
        var withFailingFilesystemProbe = new HostSandboxContainmentProbe(logger: null,
            _ => throw new InvalidOperationException("the probe exploded")).Containment;

        AssertEx.False(withFailingFilesystemProbe.SupportsFilesystemIsolation);
        AssertEx.NotNullOrEmpty(withFailingFilesystemProbe.FilesystemIsolationUnavailableReason);
        AssertEx.Contains(withFailingFilesystemProbe.FilesystemIsolationUnavailableReason, "the probe exploded");

        AssertEx.Equal(reference.SupportsResourceLimits, withFailingFilesystemProbe.SupportsResourceLimits);
        AssertEx.Equal(reference.SupportsNetworkIsolation, withFailingFilesystemProbe.SupportsNetworkIsolation);
        AssertEx.Equal(reference.SupportsProcessGroup, withFailingFilesystemProbe.SupportsProcessGroup);
        AssertEx.Equal<string?>(reference.ResourceLimitsUnavailableReason, withFailingFilesystemProbe.ResourceLimitsUnavailableReason);
        AssertEx.Equal<string?>(reference.NetworkIsolationUnavailableReason, withFailingFilesystemProbe.NetworkIsolationUnavailableReason);

        await Task.CompletedTask;
    }

    [Test]
    public async Task ANonIsolatedRequest_ProducesTheExactChainItProducedBeforeThisModeExisted()
    {
        // The regression guard for every existing consumer. AgentHome, Coder and Development Mode all create sandboxes
        // without naming an isolation mode, and their launch chain must be byte-identical to the pre-existing one.
        var containment = new SandboxContainment
        {
            SupportsProcessGroup = true,
            SupportsResourceLimits = true,
            SupportsNetworkIsolation = true,
            SetsidPath = "/usr/bin/setsid",
            SystemdRunPath = "/usr/bin/systemd-run",
            UnsharePath = "/usr/bin/unshare",
            EnvPath = "/usr/bin/env"
        };
        var policy = new SandboxLaunchPolicy
        {
            ResourceLimits = new SandboxResourceLimits { MemoryMb = 512 },
            DenyNetworkEgress = true
        };

        var descriptor = SandboxLaunchPlan.Create("/bin/echo", ["hello"], policy, containment);

        AssertEx.Equal("/usr/bin/setsid", descriptor.FileName);
        AssertEx.Equal(string.Join(' ',
                "/usr/bin/systemd-run --scope --user -q -p MemoryMax=512M -p MemorySwapMax=0 --",
                "/usr/bin/unshare --user --map-current-user --net --",
                "/usr/bin/env -u XDG_RUNTIME_DIR -u DBUS_SESSION_BUS_ADDRESS --",
                "/bin/echo hello"),
            string.Join(' ', descriptor.Arguments));
        AssertEx.False(descriptor.AppliedFilesystemIsolation);
        AssertEx.Null(descriptor.ScopeUnitName);
        AssertEx.Null(descriptor.LaunchResources);

        await Task.CompletedTask;
    }

    [Test]
    public async Task CreateOrAttach_RefusesFilesystemIsolation_OnAHostThatCannotDeliverIt()
    {
        using var provider = CreateProvider(new StubProbe(SandboxContainment.None with
        {
            FilesystemIsolationUnavailableReason = "no root-owned bwrap was found"
        }));

        var exception = await AssertEx.ThrowsAsync<SandboxCapabilityNotSupportedException>(() =>
            provider.CreateOrAttachAsync(IsolatedRequest()));

        // The measured reason travels with the refusal: a caller that has to decide whether to fall back needs to
        // know WHY, and an operator reading a log needs to know what to install.
        AssertEx.Contains(exception.Message, "no root-owned bwrap was found");

        await Task.CompletedTask;
    }

    [Test]
    public async Task CreateOrAttach_RefusesReadOnlyTrees_WithoutFilesystemIsolation()
    {
        using var provider = CreateProvider(new StubProbe(SandboxContainment.None));

        var exception = await AssertEx.ThrowsAsync<SandboxCapabilityNotSupportedException>(() =>
            provider.CreateOrAttachAsync(IsolatedRequest() with
            {
                Isolation = SandboxIsolationMode.None,
                ReadOnlyTrees = ["/opt/xe/venv"]
            }));

        AssertEx.Contains(exception.Message, "read-only trees");

        await Task.CompletedTask;
    }

    [Test]
    public async Task Capabilities_AdvertiseTheFilesystemBoundary_IfAndOnlyIfTheProbeMeasuredIt()
    {
        using var without = CreateProvider(new StubProbe(SandboxContainment.None));
        AssertEx.False(without.Capabilities.HasFlag(SandboxProviderCapabilities.SupportsFilesystemIsolation));

        using var with = CreateProvider(new StubProbe(SandboxContainment.None with { FilesystemIsolation = FakeIsolation() }));
        AssertEx.True(with.Capabilities.HasFlag(SandboxProviderCapabilities.SupportsFilesystemIsolation));

        await Task.CompletedTask;
    }

    [Test]
    public async Task CreateRequest_RefusesANonPositiveThreadLimit()
    {
        AssertEx.Throws<ArgumentOutOfRangeException>(() => _ = IsolatedRequest() with { ThreadLimit = 0 });
        AssertEx.Throws<ArgumentOutOfRangeException>(() => _ = IsolatedRequest() with { ThreadLimit = -1 });

        await Task.CompletedTask;
    }

    [Test]
    public async Task CanBindReadOnlyTree_RefusesEveryMountPointTheChainOwns()
    {
        // Two different failure shapes hide behind this one rule: a tree under /usr or /proc cannot be mounted at all,
        // while a tree under /tmp or /work is mounted and then silently shadowed by the jail. The second is the
        // dangerous one, and it is why the rule rejects rather than reorders.
        AssertEx.False(SandboxIsolatedChain.CanBindReadOnlyTree("/tmp/xe-agent-home-sandboxes/tree"));
        AssertEx.False(SandboxIsolatedChain.CanBindReadOnlyTree("/work/tree"));
        AssertEx.False(SandboxIsolatedChain.CanBindReadOnlyTree("/usr/lib/xe"));
        AssertEx.False(SandboxIsolatedChain.CanBindReadOnlyTree("/proc"));
        AssertEx.False(SandboxIsolatedChain.CanBindReadOnlyTree("/lib64/xe"));
        AssertEx.False(SandboxIsolatedChain.CanBindReadOnlyTree("/"));

        AssertEx.True(SandboxIsolatedChain.CanBindReadOnlyTree("/home/user/.local/share/xe/compute-runtime/venv"));
        AssertEx.True(SandboxIsolatedChain.CanBindReadOnlyTree("/var/lib/xe/venv"));
        AssertEx.True(SandboxIsolatedChain.CanBindReadOnlyTree("/opt/xe/venv"));

        await Task.CompletedTask;
    }

    [Test]
    public async Task ProbeScript_QuotesEveryHostPath_SoAnApostropheCannotEndTheQuote()
    {
        // The paths the probe script names are HOST data: the engine user's home, the sandbox container root, the
        // XDG_RUNTIME_DIR. Pasted between two apostrophes, one apostrophe inside any of them closes the quote the
        // script text opened and the rest of that line becomes shell syntax — the chain then fails and the probe
        // reports a perfectly capable host as unable to isolate.
        var script = HostSandboxFilesystemIsolationProbe.BuildProbeScript("/home/o'brien/xe probe/.canary",
            "/tmp/xe'sandboxes/canary file",
            "/run/user/1000'x",
            listenerPort: 4242,
            "/usr/bin/bash");

        AssertEx.Contains(script, @"if [ -e '/home/o'\''brien/xe probe/.canary' ]; then echo HOMECANARY=PRESENT;");
        AssertEx.Contains(script, @"if [ -e '/tmp/xe'\''sandboxes/canary file' ]; then echo SIBLINGCANARY=PRESENT;");
        AssertEx.Contains(script, @"if [ -e '/run/user/1000'\''x/bus' ]; then echo BUS=PRESENT;");
        AssertEx.False(script.Contains("[ -e '/home/o'brien", StringComparison.Ordinal),
            "an apostrophe must never reach the script as itself");

        await Task.CompletedTask;
    }

    [Test]
    public async Task ProbeScript_WithAHostilePath_IsAcceptedByTheShell_AndStillFindsTheFile()
    {
        // Two halves, because either alone would pass on a broken renderer: `sh -n` proves the script PARSES (a
        // dangling quote makes the whole script a syntax error), and running the canary line against a real file whose
        // name carries an apostrophe and a space proves the quoting preserved the path rather than merely escaping it
        // into something else.
        if (!OperatingSystem.IsLinux())
        {
            throw new TUnit.Core.Exceptions.SkipTestException("the probe script is POSIX shell, checked with the host's /bin/sh");
        }

        var directory = Path.Combine(Path.GetTempPath(), $"xe'probe {Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var canary = Path.Combine(directory, "it's a canary");
            await File.WriteAllTextAsync(canary, "host");
            var script = HostSandboxFilesystemIsolationProbe.BuildProbeScript(canary,
                Path.Combine(directory, "absent'file"),
                directory,
                listenerPort: 4242,
                bash: null);

            AssertEx.Equal(expected: 0, RunShell(["-n", "-c", script]).ExitCode, "the rendered script must parse");

            var homeLine = script.Split('\n').First(line => line.Contains("HOMECANARY", StringComparison.Ordinal));
            var sibling = script.Split('\n').First(line => line.Contains("SIBLINGCANARY", StringComparison.Ordinal));

            AssertEx.Equal("HOMECANARY=PRESENT", RunShell(["-c", homeLine]).Output.Trim());
            AssertEx.Equal("SIBLINGCANARY=ABSENT", RunShell(["-c", sibling]).Output.Trim());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static (int ExitCode, string Output) RunShell(IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        return (process.ExitCode, output);
    }

    private static SandboxCreateRequest IsolatedRequest()
    {
        return new SandboxCreateRequest
        {
            AttachKey = new SandboxAttachKey
            {
                OwnerUserId = "owner",
                NodeId = "node",
                ProviderName = ProcessSandboxRuntimeProvider.Name,
                RuntimeProfile = "compute",
                ManifestVersion = 1
            },
            RuntimeProfile = "compute",
            NetworkPolicy = SandboxNetworkPolicy.Unrestricted,
            Isolation = SandboxIsolationMode.Filesystem
        };
    }

    private static SandboxFilesystemIsolation FakeIsolation()
    {
        return new SandboxFilesystemIsolation
        {
            SetsidPath = "/usr/bin/setsid",
            SystemdRunPath = "/usr/bin/systemd-run",
            SystemctlPath = "/usr/bin/systemctl",
            BwrapPath = "/usr/bin/bwrap",
            UsrMergeEntries = [],
            UserId = 1000,
            GroupId = 1000,
            UserBusEnvironment = new Dictionary<string, string>(StringComparer.Ordinal) { ["XDG_RUNTIME_DIR"] = "/run/user/1000" }
        };
    }

    private static ProcessSandboxRuntimeProvider CreateProvider(ISandboxContainmentProbe probe)
    {
        return new ProcessSandboxRuntimeProvider(Options.Create(new LocalContainerOptions
            {
                MaxCopyFileBytes = LocalContainerOptions.DefaultMaxCopyFileBytes,
                MaxJailDiskBytes = LocalContainerOptions.DefaultMaxJailDiskBytes
            }),
            TimeProvider.System,
            logger: null,
            new SandboxLauncher(probe));
    }

    private sealed class StubProbe : ISandboxContainmentProbe
    {
        public StubProbe(SandboxContainment containment)
        {
            Containment = containment;
        }

        public SandboxContainment Containment { get; }
    }
}
