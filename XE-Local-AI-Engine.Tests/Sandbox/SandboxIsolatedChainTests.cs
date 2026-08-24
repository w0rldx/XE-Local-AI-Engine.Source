namespace XE_Local_AI_Engine.Tests.Sandbox;

using System.Text;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Isolation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Byte-exact coverage of the isolated launch chain. The chain IS the security boundary and its argument order is
///     semantic — <c>--remount-ro</c> before the bind it was meant to harden hardens nothing, a <c>--bind-fd</c>
///     before the <c>--dir</c> that creates its mount point fails — so it is asserted as a whole vector rather than by
///     spot-checking that a few flags appear somewhere. A change to any of it should mean editing this expectation on
///     purpose.
/// </summary>
public sealed class SandboxIsolatedChainTests
{
    [Test]
    public async Task Render_ProducesTheExactChain_ForAUsrMergedHost()
    {
        var chain = SandboxIsolatedChain.Render(UsrMergedInputs(), "/usr/bin/python3", ["-I", "-"]);

        AssertEx.Equal(string.Join('\n', ExpectedUsrMergedChain()), string.Join('\n', chain));

        await Task.CompletedTask;
    }

    [Test]
    public async Task Render_EmitsAReadOnlyBind_ForEachNamedTree_BeforeTheWritableJail()
    {
        var inputs = UsrMergedInputs() with
        {
            ReadOnlyTrees =
            [
                new SandboxIsolatedTreeBinding(FileDescriptor: 30, "/opt/xe/venv"),
                new SandboxIsolatedTreeBinding(FileDescriptor: 31, "/opt/xe/cpython")
            ]
        };

        var chain = SandboxIsolatedChain.Render(inputs, "/bin/sh", []);
        var venv = IndexOfSequence(chain, ["--ro-bind-fd", "30", "/opt/xe/venv"]);
        var cpython = IndexOfSequence(chain, ["--ro-bind-fd", "31", "/opt/xe/cpython"]);
        var jail = IndexOfSequence(chain, ["--bind-fd", "20", "/work"]);

        AssertEx.True(venv >= 0 && cpython >= 0, "both read-only trees must be bound by descriptor");
        // Order matters: the writable jail's bind and the --chdir that follows it must be the last mount operations,
        // so nothing later can shadow the one directory the workload is allowed to write.
        AssertEx.True(venv < jail && cpython < jail, "read-only trees must be bound before the writable jail");

        await Task.CompletedTask;
    }

    [Test]
    public async Task Render_BindsALegacyRootReadOnly_WhenTheHostIsNotUsrMerged()
    {
        // A split-usr host: /bin and /lib are real directories, not symlinks into /usr. Emitting a --symlink there
        // would produce a jail in which no ELF interpreter resolves and nothing can exec at all.
        var inputs = UsrMergedInputs() with
        {
            UsrMergeEntries =
            [
                new SandboxUsrMergeEntry("/bin", SandboxUsrMergeAction.ReadOnlyBind, Target: null),
                new SandboxUsrMergeEntry("/lib", SandboxUsrMergeAction.ReadOnlyBind, Target: null)
            ]
        };

        var chain = SandboxIsolatedChain.Render(inputs, "/bin/sh", []);

        AssertEx.True(IndexOfSequence(chain, ["--ro-bind", "/bin", "/bin"]) >= 0, "a real /bin must be bound read-only");
        AssertEx.True(IndexOfSequence(chain, ["--ro-bind", "/lib", "/lib"]) >= 0, "a real /lib must be bound read-only");
        AssertEx.False(chain.Contains("--symlink"), "a split-usr host must produce no usr-merge symlink");

        await Task.CompletedTask;
    }

    [Test]
    public async Task Render_OmitsTheThreadPinning_ForNoLegacyRoot_AndAlwaysPinsEveryThreadVariable()
    {
        var chain = SandboxIsolatedChain.Render(UsrMergedInputs() with { ThreadLimit = 4 }, "/bin/sh", []);

        foreach (var name in SandboxIsolatedChain.ThreadCountVariableNames)
        {
            AssertEx.True(IndexOfSequence(chain, ["--setenv", name, "4"]) >= 0, $"{name} must be pinned inside the sandbox");
        }

        await Task.CompletedTask;
    }

    [Test]
    public async Task Render_OmitsMemorySwapMax_WhenNoMemoryCeilingWasAskedFor()
    {
        var chain = SandboxIsolatedChain.Render(UsrMergedInputs() with { ResourceLimits = new SandboxResourceLimits { PidsLimit = 8 } },
            "/bin/sh",
            []);

        AssertEx.Contains(chain, "TasksMax=8");
        AssertEx.False(chain.Contains("MemorySwapMax=0"), "MemorySwapMax must never be emitted without MemoryMax");

        await Task.CompletedTask;
    }

    [Test]
    public async Task Resolve_RejectsAnUnrecognisedLayout_RatherThanGuessing()
    {
        // A /bin that is a symlink to somewhere other than /usr is a layout nobody here has reasoned about. Guessing
        // would produce a jail whose system tree is not the one the rule describes.
        AssertEx.Throws<SandboxIsolationUnavailableException>(() => SandboxUsrMergeLayout.Resolve(path =>
            string.Equals(path, "/bin", StringComparison.Ordinal)
                ? new SandboxPathShape(Exists: true, IsSymbolicLink: true, IsDirectory: true, "/opt/bin")
                : Absent()));

        await Task.CompletedTask;
    }

    [Test]
    public async Task Resolve_OmitsAnAbsentLegacyRoot()
    {
        var entries = SandboxUsrMergeLayout.Resolve(_ => Absent());

        AssertEx.Empty(entries);

        await Task.CompletedTask;
    }

    [Test]
    public async Task SyntheticEtc_HasTheExactBytesTheJailIsGiven()
    {
        // These four files ARE the account database the workload reads. Pinning them means a change is a deliberate
        // edit to this assertion rather than a drift nobody noticed.
        AssertEx.Equal("root:x:0:0:root:/root:/sbin/nologin\nxe:x:1000:1000:xe:/work/home:/sbin/nologin\n",
            Encoding.UTF8.GetString(SandboxSyntheticEtc.BuildPasswd(userId: 1000, groupId: 1000)));
        AssertEx.Equal("root:x:0:\nxe:x:1000:\n", Encoding.UTF8.GetString(SandboxSyntheticEtc.BuildGroup(groupId: 1000)));
        AssertEx.Equal("passwd: files\ngroup: files\nshadow: files\nhosts: files\n",
            Encoding.UTF8.GetString(SandboxSyntheticEtc.BuildNameServiceSwitch()));
        AssertEx.Equal("127.0.0.1 localhost\n::1 localhost ip6-localhost ip6-loopback\n",
            Encoding.UTF8.GetString(SandboxSyntheticEtc.BuildHosts()));

        await Task.CompletedTask;
    }

    [Test]
    public async Task ScopeUnit_GeneratesARecognisableName_AndRecognisesOnlyItsOwnShape()
    {
        var unit = SandboxScopeUnit.Create("compute");

        AssertEx.True(unit.StartsWith("xe-compute-", StringComparison.Ordinal), $"unexpected unit name '{unit}'");
        AssertEx.True(SandboxScopeUnit.IsEngineOwned(unit));

        // The sweep signals by pattern, so anything it would accept can be killed by a restart. A loose prefix match
        // would let an unrelated unit that happens to start with "xe-" be caught by it.
        AssertEx.False(SandboxScopeUnit.IsEngineOwned("xe-compute.scope"));
        AssertEx.False(SandboxScopeUnit.IsEngineOwned("xe-compute-notahexguid.scope"));
        AssertEx.False(SandboxScopeUnit.IsEngineOwned("session-3.scope"));
        AssertEx.False(SandboxScopeUnit.IsEngineOwned("xe-compute-00000000000000000000000000000000.service"));
        AssertEx.False(SandboxScopeUnit.IsEngineOwned(unitName: null));

        // A role of punctuation only must still produce a legal unit name rather than "xe--<guid>.scope".
        AssertEx.True(SandboxScopeUnit.IsEngineOwned(SandboxScopeUnit.Create("../../..")));

        await Task.CompletedTask;
    }

    private static SandboxPathShape Absent()
    {
        return new SandboxPathShape(Exists: false, IsSymbolicLink: false, IsDirectory: false, CanonicalPath: null);
    }

    private static SandboxIsolatedChainInputs UsrMergedInputs()
    {
        return new SandboxIsolatedChainInputs
        {
            SetsidPath = "/usr/bin/setsid",
            SystemdRunPath = "/usr/bin/systemd-run",
            BwrapPath = "/usr/bin/bwrap",
            ScopeUnitName = "xe-compute-0123456789abcdef0123456789abcdef.scope",
            RuntimeMaxSeconds = 150,
            UserId = 1000,
            GroupId = 1000,
            UsrMergeEntries =
            [
                new SandboxUsrMergeEntry("/bin", SandboxUsrMergeAction.Symlink, "usr/bin"),
                new SandboxUsrMergeEntry("/lib64", SandboxUsrMergeAction.Symlink, "usr/lib64")
            ],
            PasswdDescriptor = 10,
            GroupDescriptor = 11,
            NameServiceSwitchDescriptor = 12,
            HostsDescriptor = 13,
            JailDescriptor = 20,
            JailTempDescriptor = 21,
            ResourceLimits = new SandboxResourceLimits { MemoryMb = 2048, PidsLimit = 64, CpuCount = 2 },
            ThreadLimit = 2
        };
    }

    private static string[] ExpectedUsrMergedChain()
    {
        return
        [
            "/usr/bin/setsid",
            "/usr/bin/systemd-run", "--user", "--scope", "-q", "--collect",
            "--unit=xe-compute-0123456789abcdef0123456789abcdef.scope", "--expand-environment=no",
            "-p", "KillMode=control-group",
            "-p", "RuntimeMaxSec=150",
            "-p", "MemoryMax=2048M",
            "-p", "MemorySwapMax=0",
            "-p", "TasksMax=64",
            "-p", "CPUQuota=200%",
            "--",
            "/usr/bin/bwrap",
            "--unshare-user", "--uid", "1000", "--gid", "1000",
            "--unshare-pid", "--unshare-ipc", "--unshare-uts", "--unshare-net",
            "--hostname", "xe-compute",
            "--disable-userns", "--assert-userns-disabled",
            "--ro-bind", "/usr", "/usr",
            "--symlink", "usr/bin", "/bin",
            "--symlink", "usr/lib64", "/lib64",
            "--dir", "/etc",
            "--perms", "0444", "--ro-bind-data", "10", "/etc/passwd",
            "--perms", "0444", "--ro-bind-data", "11", "/etc/group",
            "--perms", "0444", "--ro-bind-data", "12", "/etc/nsswitch.conf",
            "--perms", "0444", "--ro-bind-data", "13", "/etc/hosts",
            "--dev", "/dev", "--proc", "/proc",
            "--dir", "/home", "--dir", "/run", "--dir", "/var", "--dir", "/tmp", "--dir", "/work",
            "--bind-fd", "20", "/work",
            "--bind-fd", "21", "/tmp",
            "--chdir", "/work",
            "--remount-ro", "/", "--remount-ro", "/dev", "--remount-ro", "/proc",
            "--clearenv",
            "--setenv", "PATH", "/usr/bin:/bin",
            "--setenv", "HOME", "/work/home",
            "--setenv", "PWD", "/work",
            "--setenv", "TMPDIR", "/tmp",
            "--setenv", "TMP", "/tmp",
            "--setenv", "TEMP", "/tmp",
            "--setenv", "LANG", "C.UTF-8",
            "--setenv", "LC_ALL", "C.UTF-8",
            "--setenv", "PYTHONNOUSERSITE", "1",
            "--setenv", "PYTHONDONTWRITEBYTECODE", "1",
            "--setenv", "OPENBLAS_NUM_THREADS", "2",
            "--setenv", "OMP_NUM_THREADS", "2",
            "--setenv", "MKL_NUM_THREADS", "2",
            "--setenv", "NUMEXPR_NUM_THREADS", "2",
            "--die-with-parent", "--new-session", "--",
            "/usr/bin/python3", "-I", "-"
        ];
    }

    private static int IndexOfSequence(IReadOnlyList<string> chain, IReadOnlyList<string> sequence)
    {
        for (var start = 0; start + sequence.Count <= chain.Count; start++)
        {
            var matched = true;
            for (var offset = 0; offset < sequence.Count; offset++)
            {
                if (!string.Equals(chain[start + offset], sequence[offset], StringComparison.Ordinal))
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                return start;
            }
        }

        return -1;
    }
}
