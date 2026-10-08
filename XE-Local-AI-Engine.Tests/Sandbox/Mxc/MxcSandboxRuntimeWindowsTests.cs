namespace XE_Local_AI_Engine.Tests.Sandbox.Mxc;

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Mxc;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

/// <summary>
///     The real MXC SDK on a real Windows host: the package's native library loads, the probe answers for the engine's policy, and a
///     contained <c>cmd.exe</c> can write its jail but cannot read outside it or open a loopback connection.
/// </summary>
/// <remarks>
///     The execution test needs ProcessContainer support plus the one-time admin host prep (<c>wxc-host-prep prepare-system-drive</c>
///     and the per-boot <c>prepare-null-device</c>); without either it skips with MXC's own reason, never passes. The
///     <c>windows-sandbox</c> CI leg runs the prep first and fails if this test skips.
/// </remarks>
[Category(TestCategories.Integration)]
[RunOn(OS.Windows)]
[SupportedOSPlatform("windows10.0.26100.0")]
public sealed class MxcSandboxRuntimeWindowsTests
{
    private static readonly TimeSpan RunBudget = TimeSpan.FromSeconds(60);

    [Test]
    public async Task GetPlatformSupport_LoadsTheNativeLibrary_AndAnswers()
    {
        var support = new MxcSandboxRuntime().GetPlatformSupport();

        AssertEx.NotNull(support);
        AssertEx.True(support.IsSupported || !string.IsNullOrEmpty(support.Reason), "an unsupported platform must say why");
        await Task.CompletedTask;
    }

    [Test]
    public async Task Probe_ForTheEnginePolicy_ReturnsATierOrAnError()
    {
        var runtime = new MxcSandboxRuntime();
        var support = runtime.GetPlatformSupport();
        Skip.Unless(support.IsSupported, $"MXC reports this platform unsupported: {support.Reason}");

        var probe = runtime.Probe(MxcPolicyMapper.BuildProbeRequest());

        AssertEx.True(probe.Tier is not null ^ probe.Error is not null, "MXC must report exactly one of a tier and an error");
        var measured = MxcProbe.Measure(runtime);
        AssertEx.Equal(probe.Tier is not null, measured.Supported);
        AssertEx.Equal<string?>(probe.Tier?.ToString(), measured.Tier);
        await Task.CompletedTask;
    }

    [Test]
    public async Task ContainedCmd_WritesItsJail_CannotReadOutside_AndCannotConnect()
    {
        var runtime = new MxcSandboxRuntime();
        var probe = MxcProbe.Measure(runtime);
        Skip.Unless(OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100), "MXC ProcessContainer needs Windows build 26100 (24H2) or later.");
        Skip.Unless(probe.Supported, $"MXC ProcessContainer is unavailable here: {probe.Reason}");
        Skip.Unless(!probe.HostPrepMissing, $"MXC host prep is missing (run wxc-host-prep, elevated): {string.Join(" | ", probe.Warnings)}");
        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var curl = Path.Combine(system32, "curl.exe");
        Skip.Unless(File.Exists(curl), "curl.exe is not in System32 on this host; the network negative control needs it.");

        var root = Directory.CreateTempSubdirectory("xe-mxc-").FullName;
        try
        {
            var jail = Directory.CreateDirectory(Path.Combine(root, "jail")).FullName;
            var deniedRoot = Directory.CreateDirectory(Path.Combine(root, "denied")).FullName;
            var ungranted = Directory.CreateDirectory(Path.Combine(root, "ungranted")).FullName;
            var deniedCanary = Path.Combine(deniedRoot, "canary.txt");
            var ungrantedCanary = Path.Combine(ungranted, "canary.txt");
            await File.WriteAllTextAsync(deniedCanary, "denied-canary-secret");
            await File.WriteAllTextAsync(ungrantedCanary, "ungranted-canary-secret");

            // Positive control: the jail is writable from inside.
            var write = await RunAsync(runtime, jail, deniedRoot, "cmd.exe", ["/c", "echo", "inside>", Path.Combine(jail, "out.txt")]);
            AssertEx.Equal(0, write.ExitCode, write.Describe());
            AssertEx.Equal("inside", (await File.ReadAllTextAsync(Path.Combine(jail, "out.txt"))).Trim());

            // A denied root and a merely ungranted path are both unreadable.
            foreach (var canary in new[]
                     {
                         deniedCanary,
                         ungrantedCanary
                     })
            {
                var read = await RunAsync(runtime, jail, deniedRoot, "cmd.exe", ["/c", "type", canary]);
                AssertEx.NotEqual(0, read.ExitCode, read.Describe());
                AssertEx.False(read.Output.Contains("canary-secret", StringComparison.Ordinal), read.Describe());
            }

            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var url = string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");

            // Negative control: the same curl outside the container DOES reach the listener, so "no connection" below means the policy.
            using (var hostCurl = Process.Start(new ProcessStartInfo(curl, ["-s", "-m", "3", url])
                   {
                       UseShellExecute = false
                   })!)
            {
                using var acceptBudget = new CancellationTokenSource(RunBudget);
                using var accepted = await listener.AcceptTcpClientAsync(acceptBudget.Token);
                accepted.Close();
                await hostCurl.WaitForExitAsync().WaitAsync(RunBudget);
            }

            var version = await RunAsync(runtime, jail, deniedRoot, curl, ["--version"]);
            AssertEx.Equal(0, version.ExitCode, "curl must start inside the container, or the connect failure below proves nothing: " + version.Describe());

            var connect = await RunAsync(runtime, jail, deniedRoot, curl, ["-s", "-m", "5", url]);
            AssertEx.NotEqual(0, connect.ExitCode, connect.Describe());
            AssertEx.False(listener.Pending(), "the contained curl reached the loopback listener");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<RunResult> RunAsync(MxcSandboxRuntime runtime, string jail, string deniedRoot, string executable, IReadOnlyList<string> arguments)
    {
        var request = MxcPolicyMapper.Build(new MxcLaunchRequest
        {
            Executable = executable,
            Arguments = arguments,
            WorkingDirectory = jail,
            Environment = ChildEnvironment(jail),
            JailRoot = jail,
            ReadOnlyTrees = [],
            DeniedRoots = [deniedRoot],
            Timeout = RunBudget,
        });
        var lines = new List<string>();
        var gate = new object();

        void Capture(string line)
        {
            lock (gate)
            {
                lines.Add(line);
            }
        }

        using var cancellation = new CancellationTokenSource(RunBudget);
        using var child = new MxcChildProcess(await runtime.SpawnAsync(request, cancellation.Token), TimeProvider.System, Capture, Capture);
        var exitCode = await child.WaitForExitAsync(cancellation.Token);
        lock (gate)
        {
            return new RunResult
            {
                ExitCode = exitCode,
                Output = string.Join('\n', lines),
                Warnings = child.Warnings
            };
        }
    }

    /// <summary>
    ///     MXC merges nothing into a supplied environment, so the Windows basics are passed explicitly; temp and the profile trio point
    ///     into the jail as <c>SandboxLauncher</c> composes them. The SDK refuses an environment without <c>LOCALAPPDATA</c>.
    /// </summary>
    private static Dictionary<string, string> ChildEnvironment(string jail)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[]
                 {
                     "SystemRoot",
                     "windir",
                     "ComSpec",
                     "PATHEXT",
                     "SystemDrive",
                     "ProgramData"
                 })
        {
            if (Environment.GetEnvironmentVariable(name) is { } value)
            {
                environment[name] = value;
            }
        }

        environment["PATH"] = Environment.GetFolderPath(Environment.SpecialFolder.System);
        environment["TEMP"] = jail;
        environment["TMP"] = jail;
        environment["USERPROFILE"] = jail;
        environment["APPDATA"] = jail;
        environment["LOCALAPPDATA"] = jail;
        return environment;
    }

    private sealed class RunResult
    {
        public required int ExitCode { get; init; }

        public required string Output { get; init; }

        public required IReadOnlyList<string> Warnings { get; init; }

        public string Describe() =>
            $"exit {ExitCode}; output: {Output}; MXC warnings: {string.Join(" | ", Warnings)}";
    }
}
