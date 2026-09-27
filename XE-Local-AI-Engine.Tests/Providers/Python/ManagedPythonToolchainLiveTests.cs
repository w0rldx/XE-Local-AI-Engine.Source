namespace XE_Local_AI_Engine.Tests.Providers.Python;

using System.Collections.Concurrent;
using TUnit.Core.Exceptions;
using XE_Local_AI_Engine.Providers.Python;
using XE_Local_AI_Engine.Providers.Python.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     LIVE evidence for ADR 0016 §3's concurrency claim: two real <c>uv sync</c> processes against one EMPTY shared store.
/// </summary>
/// <remarks>
///     Training and the compute tool (or two hosts) do exactly this. uv documents its cache as safe for concurrent use; its
///     CPython install directory's lock is undocumented, so this run is the proof. Opt-in on <c>XE_COMPUTE_LIVE=1</c>: it
///     downloads uv, a CPython and a wheel.
/// </remarks>
[Category(TestCategories.ExternalInfra)]
public sealed class ManagedPythonToolchainLiveTests : IDisposable
{
    private const string EnabledVariable = "XE_COMPUTE_LIVE";

    private static readonly TimeSpan StepTimeout = TimeSpan.FromMinutes(10);

    private readonly HttpClient _http = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "xe-python-store-live-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _http.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public async Task TwoUvSyncProcesses_OnOneEmptyStore_BothSucceed_AndShareOneCPython()
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new SkipTestException("the managed Python toolchain is Linux-only (the uv pin and the process runner both are).");
        }

        if (!string.Equals(Environment.GetEnvironmentVariable(EnabledVariable), "1", StringComparison.Ordinal))
        {
            throw new SkipTestException($"set {EnabledVariable}=1 to allow this test to download uv, a CPython and a wheel.");
        }

        var toolchain = new ManagedPythonToolchain(Path.Combine(_root, "python"));
        var uv = await new UvBinaryAcquirer(_http).EnsureUvAsync(toolchain.Root, _ => { }, CancellationToken.None);
        var runner = new LinuxPythonToolRunner();
        var log = new ConcurrentQueue<string>();

        // Two DIFFERENT projects that need the same interpreter and the same wheel, so both processes race to install
        // one CPython and to populate one cache entry.
        var syncs = new[]
        {
            "alpha",
            "beta"
        }.Select(async name =>
        {
            var project = Path.Combine(_root, name);
            var home = Directory.CreateDirectory(Path.Combine(project, ".work", ".home")).FullName;
            var tmp = Directory.CreateDirectory(Path.Combine(project, ".work", ".tmp")).FullName;
            await File.WriteAllTextAsync(Path.Combine(project, "pyproject.toml"),
                $"""
                 [project]
                 name = "xe-store-{name}"
                 version = "0"
                 requires-python = ">=3.13,<3.14"
                 dependencies = ["six==1.17.0"]
                 """);
            var exit = await runner.RunAsync(uv,
                ["sync", "--project", project],
                ManagedPythonEnvironment.BuildUvEnvironment(home, tmp, toolchain.CacheDirectory, toolchain.PythonInstallDirectory),
                project,
                line => log.Enqueue($"{name}: {line}"),
                StepTimeout,
                CancellationToken.None);
            return (project, exit);
        }).ToArray();

        var results = await Task.WhenAll(syncs);

        AssertEx.True(results.All(static result => result.exit == 0), "both syncs must succeed; log:\n" + string.Join('\n', log));
        foreach (var (project, _) in results)
        {
            var output = new List<string>();
            var exit = await runner.RunAsync(Path.Combine(project, ".venv", "bin", "python"),
                ["-c", "import six, sys; print(six.__version__, sys.version_info[:2])"],
                ManagedPythonEnvironment.BuildAllowlisted(),
                project,
                output.Add,
                StepTimeout,
                CancellationToken.None);
            AssertEx.Equal(0, exit, string.Join('\n', output));
            AssertEx.Contains(string.Join('\n', output), "1.17.0 (3, 13)");
        }

        var installs = Directory.GetDirectories(toolchain.PythonInstallDirectory, "cpython-3.13.*");
        AssertEx.Equal(1, installs.Length, "one CPython minor serves both environments: " + string.Join(", ", installs));
    }
}
