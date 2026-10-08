namespace XE_Local_AI_Engine.Tests.Sandbox.Mxc;

using System.Diagnostics;
using Microsoft.Mxc.Sdk.V1;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Isolation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="SandboxLauncher" />'s AppContainer branch: a policy resolved to the Windows boundary yields an MXC request instead of an
///     argv chain, with the jail environment overlay, the deny roots that cannot shadow a grant, and none of the Linux kill authorities.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class SandboxLauncherAppContainerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("xe-mxc-launcher-").FullName;

    public void Dispose() =>
        Directory.Delete(_root, recursive: true);

    [Test]
    public async Task Apply_UnderTheAppContainerBoundary_BuildsTheMxcRequest_AndNoLinuxKillAuthority()
    {
        var jail = Directory.CreateDirectory(Path.Combine(_root, "jail")).FullName;
        var tree = Directory.CreateDirectory(Path.Combine(_root, "venv")).FullName;
        var startInfo = StartInfo(jail, "python.exe", "-c", "print(1)");

        var descriptor = Launcher([]).Apply(startInfo, Policy(tree), new SandboxLaunchContext
        {
            JailRoot = jail,
            CommandTimeout = TimeSpan.FromSeconds(30)
        });

        var request = AssertEx.NotNull(descriptor.MxcRequest);
        AssertEx.True(request.Containment is Containment.ProcessContainer);
        AssertEx.Equal("python.exe -c print(1)", request.Command);
        AssertEx.Equal(jail, request.WorkingDirectory);
        AssertEx.Equal(30_000u, request.TimeoutMs!.Value);
        AssertEx.Equal(jail, request.Filesystem!.ReadwritePaths.Single());
        AssertEx.Equal(tree, request.Filesystem.ReadonlyPaths.Single());
        // The applied record mirrors the bwrap chain's boundary flags, and nothing a Linux killer could act on.
        AssertEx.True(descriptor.AppliedFilesystemIsolation);
        AssertEx.True(descriptor.AppliedNetworkIsolation);
        AssertEx.False(descriptor.AppliedProcessGroup);
        AssertEx.False(descriptor.AppliedResourceLimits);
        AssertEx.Null(descriptor.ScopeUnitName);
        AssertEx.Null(descriptor.LaunchResources);
        AssertEx.True(Directory.Exists(Path.Combine(jail, SandboxIsolatedPaths.HomeDirectoryName)), "HOME must exist before the child starts");
        AssertEx.True(Directory.Exists(Path.Combine(jail, SandboxIsolatedPaths.TempDirectoryName)), "TEMP must exist before the child starts");
        await Task.CompletedTask;
    }

    [Test]
    public async Task Apply_WithoutAJail_FailsClosed()
    {
        var startInfo = StartInfo(_root, "cmd.exe");

        _ = AssertEx.Throws<SandboxIsolationUnavailableException>(() => Launcher([]).Apply(startInfo, Policy(), new SandboxLaunchContext()));
        await Task.CompletedTask;
    }

    [Test]
    public async Task Apply_WithResourceLimits_FailsRatherThanDroppingThem()
    {
        var jail = Directory.CreateDirectory(Path.Combine(_root, "jail")).FullName;
        var policy = Policy() with
        {
            ResourceLimits = new SandboxResourceLimits
            {
                CpuCount = 1
            }
        };

        _ = AssertEx.Throws<SandboxCapabilityNotSupportedException>(() =>
            Launcher([]).Apply(StartInfo(jail, "cmd.exe"), policy, new SandboxLaunchContext
            {
                JailRoot = jail
            }));
        await Task.CompletedTask;
    }

    [Test]
    public async Task Environment_OverlaysTheJailProfile_DropsTheRealOne_AndDefaultsPathToSystemAndGrantedTrees()
    {
        var jail = Directory.CreateDirectory(Path.Combine(_root, "jail")).FullName;
        var startInfo = StartInfo(jail, "cmd.exe");
        startInfo.Environment["SystemRoot"] = @"C:\Windows";
        startInfo.Environment["USERPROFILE"] = @"C:\Users\operator";
        startInfo.Environment["HOMEDRIVE"] = "C:";
        startInfo.Environment["HOMEPATH"] = @"\Users\operator";
        startInfo.Environment["PATH"] = @"C:\Users\operator\bin";
        var paths = SandboxIsolatedPaths.ForHostJail(jail);

        var environment = SandboxLauncher.BuildAppContainerEnvironment(startInfo, commandEnvironment: null, paths, [@"C:\venv"], threadLimit: 1);

        AssertEx.Equal(paths.Home, environment["USERPROFILE"]);
        AssertEx.Equal(paths.Home, environment["APPDATA"]);
        AssertEx.Equal(paths.Home, environment["LOCALAPPDATA"]);
        AssertEx.Equal(paths.Temp, environment["TEMP"]);
        AssertEx.Equal(paths.Temp, environment["TMP"]);
        AssertEx.False(environment.ContainsKey("HOMEDRIVE"), "HOMEDRIVE names the real profile");
        AssertEx.False(environment.ContainsKey("HOMEPATH"), "HOMEPATH names the real profile");
        AssertEx.Equal(@"C:\Windows\System32;C:\Windows;C:\venv", environment["PATH"]);
        await Task.CompletedTask;
    }

    [Test]
    public async Task Environment_TheCallersOwnVariablesWin()
    {
        var jail = Directory.CreateDirectory(Path.Combine(_root, "jail")).FullName;
        var requested = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["HOME"] = @"C:\elsewhere",
            ["PATH"] = @"C:\tools"
        };
        var startInfo = StartInfo(jail, "cmd.exe");
        foreach (var (name, value) in requested)
        {
            startInfo.Environment[name] = value;
        }

        var environment = SandboxLauncher.BuildAppContainerEnvironment(startInfo, requested, SandboxIsolatedPaths.ForHostJail(jail), [], threadLimit: 1);

        AssertEx.Equal(@"C:\elsewhere", environment["HOME"]);
        AssertEx.Equal(@"C:\tools", environment["PATH"]);
        await Task.CompletedTask;
    }

    [Test]
    public async Task Apply_PinsEveryThreadCountVariableToTheThreadLimit_AndTheCallersOwnValueWins()
    {
        var jail = Directory.CreateDirectory(Path.Combine(_root, "jail")).FullName;
        var startInfo = StartInfo(jail, "python.exe");
        startInfo.Environment["OMP_NUM_THREADS"] = "8";
        var context = new SandboxLaunchContext
        {
            JailRoot = jail,
            CommandEnvironment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["OMP_NUM_THREADS"] = "8"
            }
        };

        var descriptor = Launcher([]).Apply(startInfo, Policy() with
        {
            ThreadLimit = 3
        }, context);

        var environment = AssertEx.NotNull(AssertEx.NotNull(descriptor.MxcRequest).Environment);
        foreach (var name in SandboxIsolatedChain.ThreadCountVariableNames.Where(name => name != "OMP_NUM_THREADS"))
        {
            AssertEx.Equal("3", environment[name], name);
        }

        AssertEx.Equal("8", environment["OMP_NUM_THREADS"]);
        await Task.CompletedTask;
    }

    [Test]
    public async Task DeniedRoots_DropEveryAncestorOfTheJailOrAGrantedTree_AndKeepTheRest()
    {
        var profile = Directory.CreateDirectory(Path.Combine(_root, "profile")).FullName;
        var jail = Directory.CreateDirectory(Path.Combine(profile, "temp", "jail")).FullName;
        var dataRoot = Directory.CreateDirectory(Path.Combine(_root, "data")).FullName;
        var venv = Directory.CreateDirectory(Path.Combine(dataRoot, "venv")).FullName;
        var engine = Directory.CreateDirectory(Path.Combine(_root, "engine")).FullName;
        var startInfo = StartInfo(jail, "cmd.exe");

        var descriptor = Launcher([profile, dataRoot, engine + Path.DirectorySeparatorChar])
            .Apply(startInfo, Policy(venv), new SandboxLaunchContext
            {
                JailRoot = jail
            });

        // A DENY ACE on an ancestor could beat the grant beneath it; the default deny still covers both.
        AssertEx.Equal(engine, string.Join('|', descriptor.MxcRequest!.Filesystem!.DeniedPaths));
        AssertEx.Equal(engine, string.Join('|', SandboxLauncher.ResolveDeniedRoots([profile, dataRoot, engine], jail, [venv])));
        AssertEx.Equal(string.Join('|', profile, dataRoot, engine),
            string.Join('|', SandboxLauncher.ResolveDeniedRoots([profile, dataRoot, engine], Path.Combine(_root, "elsewhere"), [])));
        await Task.CompletedTask;
    }

    [Test]
    public async Task Apply_WithoutTheBoundary_KeepsTheExistingPaths()
    {
        var startInfo = StartInfo(_root, "/bin/true");

        var descriptor = Launcher([]).Apply(startInfo, SandboxLaunchPolicy.Unconstrained);

        AssertEx.Null(descriptor.MxcRequest);
        AssertEx.Equal("/bin/true", descriptor.FileName);
        await Task.CompletedTask;
    }

    private static SandboxLauncher Launcher(IReadOnlyList<string> deniedRootCandidates) =>
        new(new StubProbe(SandboxContainment.None with
        {
            AppContainerBoundary = Boundary()
        }), deniedRootCandidates);

    private static SandboxLaunchPolicy Policy(params string[] readOnlyTrees) =>
        new()
        {
            Isolation = SandboxIsolationMode.Filesystem,
            DenyNetworkEgress = true,
            ReadOnlyTrees = readOnlyTrees,
            AppContainerBoundary = Boundary()
        };

    private static SandboxAppContainerBoundary Boundary() =>
        new()
        {
            Mechanism = "mxc-processcontainer",
            Tier = "AppContainerDacl",
            Maturity = SandboxMechanismMaturity.Preview
        };

    private static ProcessStartInfo StartInfo(string workingDirectory, string executable, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false
        };
        startInfo.Environment.Clear();
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
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
