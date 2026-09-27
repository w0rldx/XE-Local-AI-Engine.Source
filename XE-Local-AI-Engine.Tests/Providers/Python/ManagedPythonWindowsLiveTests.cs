namespace XE_Local_AI_Engine.Tests.Providers.Python;

using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;
using TUnit.Core.Exceptions;
using XE_Local_AI_Engine.Providers.Python;
using XE_Local_AI_Engine.Providers.Python.Contracts;
using XE_Local_AI_Engine.Providers.Python.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

/// <summary>
///     LIVE Windows evidence for ADR 0016 M4: the shared layer provisions a throwaway pure-Python profile, a cancel
///     tree-kills uv and its interpreter, and no PEP 514 registration is written.
/// </summary>
/// <remarks>
///     <c>[RunOn(OS.Windows)]</c> reports these skipped, with a reason, on every other host; <c>[SupportedOSPlatform]</c>
///     satisfies CA1416 for the registry reads. Opt-in on <c>XE_COMPUTE_LIVE=1</c>: it downloads uv, a CPython and a wheel.
/// </remarks>
[RunOn(OS.Windows)]
[SupportedOSPlatform("windows")]
[TUnit.Core.Category(TestCategories.ExternalInfra)]
public sealed class ManagedPythonWindowsLiveTests : IDisposable
{
    private const string EnabledVariable = "XE_COMPUTE_LIVE";
    private const string AstralRegistryKey = @"Software\Python\Astral";

    private static readonly TimeSpan StepTimeout = TimeSpan.FromMinutes(10);

    private readonly HttpClient _http = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "xe-py-win-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        _http.Dispose();
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A read-only cache entry left behind in %TEMP% is not a test failure.
        }
    }

    [Test]
    public async Task Provision_ImportsTheLockedWheel_Cancel_TreeKillsUv_AndNothingIsRegistered()
    {
        AssertEx.True(OperatingSystem.IsWindows(), "[RunOn(OS.Windows)] did not engage.");
        if (!string.Equals(Environment.GetEnvironmentVariable(EnabledVariable), "1", StringComparison.Ordinal))
        {
            throw new SkipTestException($"set {EnabledVariable}=1 to allow this test to download uv, a CPython and a wheel.");
        }

        var registeredBefore = AstralRegistrations();
        var toolchain = new ManagedPythonToolchain(Path.Combine(_root, "python"));
        var uv = await new UvBinaryAcquirer(_http).EnsureUvAsync(toolchain.Root, _ => { }, CancellationToken.None);
        AssertEx.Equal(toolchain.PinnedUvExecutable, uv);
        var runner = PythonToolRunner.ForCurrentPlatform();
        AssertEx.True(runner is WindowsPythonToolRunner, "the factory must pick the Windows runner on Windows");

        var project = Path.Combine(_root, "profile");
        var home = Directory.CreateDirectory(Path.Combine(project, ".work", ".home")).FullName;
        var tmp = Directory.CreateDirectory(Path.Combine(project, ".work", ".tmp")).FullName;
        await File.WriteAllTextAsync(Path.Combine(project, "pyproject.toml"),
            """
            [project]
            name = "xe-windows-probe"
            version = "0"
            requires-python = ">=3.13,<3.14"
            dependencies = ["sympy==1.14.0"]
            """);
        var uvEnvironment = ManagedPythonEnvironment.BuildUvEnvironment(home, tmp, toolchain.CacheDirectory, toolchain.PythonInstallDirectory);

        var log = new ConcurrentQueue<string>();
        var syncExit = await runner.RunAsync(uv, ["sync", "--project", project], uvEnvironment, project, log.Enqueue, StepTimeout, CancellationToken.None);
        AssertEx.Equal(0, syncExit, "uv sync failed; log:\n" + string.Join('\n', log));
        AssertEx.True(File.Exists(Path.Combine(project, "uv.lock")), "the sync must have locked the profile");

        var interpreter = ManagedPythonToolchain.VenvInterpreterPath(Path.Combine(project, ".venv"));
        var output = new ConcurrentQueue<string>();
        var importExit = await runner.RunAsync(interpreter,
            ["-c", "import sympy, sys; print(sympy.__version__, sys.version_info[:2])"],
            ManagedPythonEnvironment.BuildAllowlisted(),
            project,
            output.Enqueue,
            StepTimeout,
            CancellationToken.None);
        AssertEx.Equal(0, importExit, string.Join('\n', output));
        AssertEx.Contains(string.Join('\n', output), "1.14.0 (3, 13)");

        await AssertCancelTreeKillsAsync(runner, uv, project, uvEnvironment);

        AssertEx.Equal(string.Join(", ", registeredBefore), string.Join(", ", AstralRegistrations()),
            $@"HKCU\{AstralRegistryKey} gained an entry: UV_PYTHON_INSTALL_REGISTRY=0 did not hold");
        AssertEx.Empty(InstallPathsUnder(_root), "an existing registration was overwritten to point into the test store");
    }

    // uv run syncs, then spawns the venv's python.exe launcher, which spawns the base interpreter: a real three-level tree.
    private async Task AssertCancelTreeKillsAsync(IPythonToolRunner runner, string uv, string project, IReadOnlyDictionary<string, string> uvEnvironment)
    {
        using var cts = new CancellationTokenSource();
        var run = runner.RunAsync(uv,
            ["run", "--project", project, "python", "-u", "-c", "print('xe-ready'); import time; time.sleep(600)"],
            uvEnvironment,
            project,
            line =>
            {
                if (line.Contains("xe-ready", StringComparison.Ordinal))
                {
                    cts.Cancel();
                }
            },
            StepTimeout,
            cts.Token);

        _ = await AssertEx.ThrowsAsync<OperationCanceledException>(() => run);
        await AssertEx.EventuallyAsync(() => ProcessesUnder(_root).Count == 0,
            TimeSpan.FromSeconds(15),
            "a process started from the test store survived the cancel: " + string.Join(", ", ProcessesUnder(_root)));
    }

    private static List<string> ProcessesUnder(string root)
    {
        var survivors = new List<string>();
        foreach (var process in Process.GetProcessesByName("uv").Concat(Process.GetProcessesByName("python")))
        {
            using (process)
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (path is not null && path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    {
                        survivors.Add($"{process.ProcessName}#{process.Id}");
                    }
                }
                catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
                {
                    // Another user's process, or one that exited while being read.
                }
            }
        }

        return survivors;
    }

    // uv writes Software\Python\Astral\<tag>\InstallPath (default value) per PEP 514; an overwrite keeps the subkey names.
    private static List<string> InstallPathsUnder(string root)
    {
        using var astral = Registry.CurrentUser.OpenSubKey(AstralRegistryKey);
        var hits = new List<string>();
        foreach (var tag in astral?.GetSubKeyNames() ?? [])
        {
            using var installPath = astral!.OpenSubKey(tag + @"\InstallPath");
            if (installPath?.GetValue(null) is string path && path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                hits.Add(tag);
            }
        }

        return hits;
    }

    private static string[] AstralRegistrations()
    {
        using var key = Registry.CurrentUser.OpenSubKey(AstralRegistryKey);
        return key is null ? [] : key.GetSubKeyNames().Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
