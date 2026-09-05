namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

/// <summary>
///     In-app CUDA build service security + orchestration must-fixes, exercised with PATH script stubs for
///     <c>git</c>/<c>cmake</c>/<c>nvidia-smi</c> (no real toolchain needed): commit-SHA hard-fail <c>[secHIGH-1]</c>,
///     scrubbed env <c>[secHIGH-2]</c>, single-flight, and "validation fails → not recorded". POSIX-only; serialized
///     (mutates the process PATH/env to inject the stubs).
/// </summary>
[NotInParallel]
public sealed class CudaBuildServiceTests
{
    [Test]
    [ExcludeOn(OS.Windows)]
    [UnsupportedOSPlatform("windows")]
    public async Task BuildService_VerifiesCommitSha_HardFailsOnMismatch()
    {
        using var dir = new TempDir();
        var stubDir = Path.Combine(dir.Path, "stubs");
        Directory.CreateDirectory(stubDir);
        var cmakeMarker = Path.Combine(dir.Path, "cmake-ran.marker");
        WriteGitStub(stubDir, "0000000000000000000000000000000000000000", envDumpPath: null);
        WriteCmakeStub(stubDir, cmakeMarker);
        WriteNvidiaSmiStub(stubDir, "8.9");

        using var handler = new ThrowingHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        var (service, store) = CreateService(dir.Path, http);
        using var restore = PrependPath(stubDir);

        await service.StartAsync(CancellationToken.None);
        await AssertEx.EventuallyAsync(() => service.GetStatus().Terminal, TimeSpan.FromSeconds(30));

        AssertEx.Equal(CudaBuildPhase.Failed.ToString(), service.GetStatus().Phase.ToString());
        AssertEx.False(File.Exists(cmakeMarker), "cmake must not run after a commit-SHA mismatch.");
        AssertEx.Null(await store.ReadAsync(CancellationToken.None));
    }

    [Test]
    [ExcludeOn(OS.Windows)]
    [UnsupportedOSPlatform("windows")]
    public async Task BuildService_RunsWithScrubbedEnv()
    {
        using var dir = new TempDir();
        var stubDir = Path.Combine(dir.Path, "stubs");
        Directory.CreateDirectory(stubDir);
        var envDump = Path.Combine(dir.Path, "child-env.txt");
        // Correct SHA so the clone step (which dumps its env) is reached.
        WriteGitStub(stubDir, LlamaCppReleasePins.PinnedCudaSourceCommitSha, envDump);
        WriteCmakeStub(stubDir, Path.Combine(dir.Path, "cmake-ran.marker"));
        WriteNvidiaSmiStub(stubDir, "8.9");

        using var handler = new ThrowingHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        var (service, _) = CreateService(dir.Path, http);

        Environment.SetEnvironmentVariable("LD_PRELOAD", "/evil/inject.so");
        Environment.SetEnvironmentVariable("CC", "/evil/cc");
        Environment.SetEnvironmentVariable("GIT_SSH_COMMAND", "ssh -o x");
        Environment.SetEnvironmentVariable("XE_NODE_SQLITE_KEY", "super-secret");
        try
        {
            using var restore = PrependPath(stubDir);
            await service.StartAsync(CancellationToken.None);
            await AssertEx.EventuallyAsync(() => File.Exists(envDump), TimeSpan.FromSeconds(30));
        }
        finally
        {
            Environment.SetEnvironmentVariable("LD_PRELOAD", null);
            Environment.SetEnvironmentVariable("CC", null);
            Environment.SetEnvironmentVariable("GIT_SSH_COMMAND", null);
            Environment.SetEnvironmentVariable("XE_NODE_SQLITE_KEY", null);
        }

        var childEnv = await File.ReadAllTextAsync(envDump, CancellationToken.None);
        AssertEx.False(childEnv.Contains("LD_PRELOAD", StringComparison.Ordinal), "LD_PRELOAD must be scrubbed.");
        AssertEx.False(childEnv.Contains("/evil/cc", StringComparison.Ordinal), "CC must be scrubbed.");
        AssertEx.False(childEnv.Contains("GIT_SSH_COMMAND", StringComparison.Ordinal), "GIT_SSH_COMMAND must be scrubbed.");
        AssertEx.False(childEnv.Contains("super-secret", StringComparison.Ordinal), "App secrets must be scrubbed.");
        AssertEx.True(childEnv.Contains("PATH=", StringComparison.Ordinal), "PATH must be passed through.");

        await AssertEx.EventuallyAsync(() => service.GetStatus().Terminal, TimeSpan.FromSeconds(30));
    }

    [Test]
    [ExcludeOn(OS.Windows)]
    [UnsupportedOSPlatform("windows")]
    public async Task BuildService_SingleFlight_SecondStartRejected()
    {
        using var dir = new TempDir();
        var stubDir = Path.Combine(dir.Path, "stubs");
        Directory.CreateDirectory(stubDir);
        // A slow clone keeps the first build in flight while the second start is attempted.
        WriteGitStub(stubDir, LlamaCppReleasePins.PinnedCudaSourceCommitSha, envDumpPath: null, cloneSleepSeconds: 3);
        WriteCmakeStub(stubDir, Path.Combine(dir.Path, "cmake-ran.marker"));
        WriteNvidiaSmiStub(stubDir, "8.9");

        using var handler = new ThrowingHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        var (service, _) = CreateService(dir.Path, http);
        using var restore = PrependPath(stubDir);

        var first = await service.StartAsync(CancellationToken.None);
        var second = await service.StartAsync(CancellationToken.None);

        AssertEx.Equal(CudaBuildStartOutcome.Started, first);
        AssertEx.Equal(CudaBuildStartOutcome.AlreadyRunning, second);

        service.Cancel();
        await AssertEx.EventuallyAsync(() => service.GetStatus().Terminal, TimeSpan.FromSeconds(30));
    }

    [Test]
    [ExcludeOn(OS.Windows)]
    [UnsupportedOSPlatform("windows")]
    public async Task BuildService_WhenValidationFails_DoesNotRecord()
    {
        using var dir = new TempDir();
        var stubDir = Path.Combine(dir.Path, "stubs");
        Directory.CreateDirectory(stubDir);
        WriteGitStub(stubDir, LlamaCppReleasePins.PinnedCudaSourceCommitSha, envDumpPath: null);
        // The built server stub FAILS its --version self-check → adoption validation fails → nothing recorded.
        WriteCmakeStub(stubDir, Path.Combine(dir.Path, "cmake-ran.marker"), serverSmokeFails: true);
        WriteNvidiaSmiStub(stubDir, "8.9");

        using var handler = new ThrowingHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        var (service, store) = CreateService(dir.Path, http);
        using var restore = PrependPath(stubDir);

        await service.StartAsync(CancellationToken.None);
        await AssertEx.EventuallyAsync(() => service.GetStatus().Terminal, TimeSpan.FromSeconds(30));

        AssertEx.Equal(CudaBuildPhase.Failed.ToString(), service.GetStatus().Phase.ToString());
        AssertEx.Null(await store.ReadAsync(CancellationToken.None));
    }

    [Test]
    [ExcludeOn(OS.Windows)]
    [UnsupportedOSPlatform("windows")]
    public async Task CudaBuildService_WhenComputeCapMalformed_UsesDefaultArch()
    {
        using var dir = new TempDir();
        var stubDir = Path.Combine(dir.Path, "stubs");
        Directory.CreateDirectory(stubDir);
        var cmakeArgs = Path.Combine(dir.Path, "cmake-args.txt");
        WriteGitStub(stubDir, LlamaCppReleasePins.PinnedCudaSourceCommitSha, envDumpPath: null);
        WriteCmakeStub(stubDir, Path.Combine(dir.Path, "cmake-ran.marker"), argsDumpPath: cmakeArgs);
        // A malformed compute_cap must NOT reach -DCMAKE_CUDA_ARCHITECTURES — the configure step falls back to the default set.
        WriteNvidiaSmiStub(stubDir, "8;native");

        using var handler = new ThrowingHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        var (service, _) = CreateService(dir.Path, http);
        using var restore = PrependPath(stubDir);

        await service.StartAsync(CancellationToken.None);
        await AssertEx.EventuallyAsync(() => File.Exists(cmakeArgs), TimeSpan.FromSeconds(30));

        var capturedArgs = await File.ReadAllTextAsync(cmakeArgs, CancellationToken.None);
        AssertEx.True(capturedArgs.Contains("-DCMAKE_CUDA_ARCHITECTURES=75;86;89;120", StringComparison.Ordinal),
            "Configure must use the default CUDA architecture set when compute_cap is malformed.");
        AssertEx.False(capturedArgs.Contains("native", StringComparison.Ordinal),
            "The malformed compute_cap must never reach the cmake invocation.");

        await AssertEx.EventuallyAsync(() => service.GetStatus().Terminal, TimeSpan.FromSeconds(30));
        AssertEx.Equal(CudaBuildPhase.Completed, service.GetStatus().Phase);
        var allCapturedArgs = await File.ReadAllTextAsync(cmakeArgs, CancellationToken.None);
        AssertEx.True(allCapturedArgs.Contains("--target llama-server llama-fit-params llama-perplexity", StringComparison.Ordinal),
            "The source-CUDA build must compile the server, the machine-readable fit helper and the perplexity tool.");
        AssertEx.True(File.Exists(Path.Combine(dir.Path,
            "llama.cpp",
            "source-cuda",
            LlamaCppReleasePins.PinnedTag,
            "build",
            "bin",
            "llama-fit-params")));
        // The build tree is produced under a work directory and then PLACED at its managed path, so an absolute build
        // RUNPATH would point at a directory that no longer exists — llama-server then dies at startup with
        // "libllama-server-impl.so: cannot open shared object file" even though the .so sits beside it. Observed on a
        // real tree built before this flag existed. $ORIGIN keeps the placed tree self-referential.
        AssertEx.True(allCapturedArgs.Contains("-DCMAKE_BUILD_RPATH_USE_ORIGIN=ON", StringComparison.Ordinal),
            "Configure must emit an $ORIGIN-relative build RUNPATH so the placed runtime stays loadable after relocation.");
    }

    [Test]
    [ExcludeOn(OS.Windows)]
    [UnsupportedOSPlatform("windows")]
    public async Task CudaBuildService_WhenComputeCapValid_UsesDetectedArchOnly()
    {
        using var dir = new TempDir();
        var stubDir = Path.Combine(dir.Path, "stubs");
        Directory.CreateDirectory(stubDir);
        var cmakeArgs = Path.Combine(dir.Path, "cmake-args.txt");
        WriteGitStub(stubDir, LlamaCppReleasePins.PinnedCudaSourceCommitSha, envDumpPath: null);
        WriteCmakeStub(stubDir, Path.Combine(dir.Path, "cmake-ran.marker"), argsDumpPath: cmakeArgs);
        WriteNvidiaSmiStub(stubDir, "12.0");

        using var handler = new ThrowingHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        var (service, _) = CreateService(dir.Path, http);
        using var restore = PrependPath(stubDir);

        await service.StartAsync(CancellationToken.None);
        await AssertEx.EventuallyAsync(() => File.Exists(cmakeArgs), TimeSpan.FromSeconds(30));

        var capturedArgs = await File.ReadAllTextAsync(cmakeArgs, CancellationToken.None);
        AssertEx.True(capturedArgs.Contains("-DCMAKE_CUDA_ARCHITECTURES=120", StringComparison.Ordinal),
            "Configure must use the detected Blackwell architecture when compute_cap is valid.");
        AssertEx.False(capturedArgs.Contains("-DCMAKE_CUDA_ARCHITECTURES=75;86;89;120", StringComparison.Ordinal),
            "A valid compute_cap must remain authoritative instead of widening to the fallback set.");

        await AssertEx.EventuallyAsync(() => service.GetStatus().Terminal, TimeSpan.FromSeconds(30));
        AssertEx.Equal(CudaBuildPhase.Completed, service.GetStatus().Phase);
    }

    [Test]
    [ExcludeOn(OS.Windows)]
    [UnsupportedOSPlatform("windows")]
    public async Task Prereq_WhenAllPresent_CanBuildTrue()
    {
        using var dir = new TempDir();
        var stubDir = Path.Combine(dir.Path, "stubs");
        Directory.CreateDirectory(stubDir);
        foreach (var tool in new[]
                 {
                     "nvcc",
                     "cmake",
                     "gcc",
                     "g++",
                     "make",
                     "git"
                 })
        {
            WriteToolStub(stubDir, tool);
        }

        var probe = new CudaBuildPrerequisiteProbe(new StubVendorProbe(DetectedGpuVendor.Nvidia), dir.Path, requiredFreeDiskBytes: 0);
        using var restore = SetExactPath(stubDir);

        var report = await probe.ProbeAsync(CancellationToken.None);
        AssertEx.True(report.CanBuild, "All prerequisites present on Linux NVIDIA should yield canBuild=true.");
    }

    [Test]
    [ExcludeOn(OS.Windows)]
    [UnsupportedOSPlatform("windows")]
    public async Task Prereq_WhenToolMissing_ItemUnsatisfiedWithReason()
    {
        using var dir = new TempDir();
        var stubDir = Path.Combine(dir.Path, "stubs");
        Directory.CreateDirectory(stubDir);
        // Everything EXCEPT nvcc.
        foreach (var tool in new[]
                 {
                     "cmake",
                     "gcc",
                     "g++",
                     "make",
                     "git"
                 })
        {
            WriteToolStub(stubDir, tool);
        }

        var probe = new CudaBuildPrerequisiteProbe(new StubVendorProbe(DetectedGpuVendor.Nvidia), dir.Path, requiredFreeDiskBytes: 0);
        using var restore = SetExactPath(stubDir);

        var report = await probe.ProbeAsync(CancellationToken.None);
        AssertEx.False(report.CanBuild);
        var nvcc = report.Items.Single(item => item.Key == "nvcc");
        AssertEx.False(nvcc.Satisfied);
        AssertEx.NotNullOrEmpty(nvcc.Detail);
    }

    private static (CudaBuildService Service, IInstalledRuntimeStore Store) CreateService(string cacheRoot, HttpClient http)
    {
        var store = new InstalledRuntimeStore(cacheRoot);
        var signal = new CudaManagedBuildSignal();
        var manager = new LlamaCppBinaryManager(http, cacheRoot, LlamaCppReleasePins.PinnedTag,
            OSPlatform.Linux, Architecture.X64, catalog: null, store, overrideOptions: null, signal);
        var probe = new AlwaysBuildableProbe();
        var service = new CudaBuildService(probe, manager, new NullCudaBuildEventPublisher(), NullLogger<CudaBuildService>.Instance, cacheRoot);
        return (service, store);
    }

    [UnsupportedOSPlatform("windows")]
    private static void WriteToolStub(string dir, string name)
    {
        WriteScript(Path.Combine(dir, name), $"#!/bin/sh\necho '{name} stub version 1.0'\nexit 0\n");
    }

    [UnsupportedOSPlatform("windows")]
    private static void WriteNvidiaSmiStub(string dir, string computeCap)
    {
        WriteScript(Path.Combine(dir, "nvidia-smi"), $"#!/bin/sh\necho '{computeCap}'\nexit 0\n");
    }

    [UnsupportedOSPlatform("windows")]
    private static void WriteGitStub(string dir, string headSha, string? envDumpPath, int cloneSleepSeconds = 0)
    {
        var dump = envDumpPath is null ? string.Empty : $"  env > '{envDumpPath}'\n";
        var sleep = cloneSleepSeconds > 0 ? $"  sleep {cloneSleepSeconds}\n" : string.Empty;
        var script = "#!/bin/sh\n"
                     + "if [ \"$1\" = \"clone\" ]; then\n"
                     + dump
                     + sleep
                     + "  for last; do :; done\n"
                     + "  mkdir -p \"$last\"\n"
                     + "  exit 0\n"
                     + "fi\n"
                     + "if [ \"$1\" = \"-C\" ]; then\n"
                     + $"  echo '{headSha}'\n"
                     + "  exit 0\n"
                     + "fi\n"
                     + "exit 0\n";
        WriteScript(Path.Combine(dir, "git"), script);
    }

    [UnsupportedOSPlatform("windows")]
    private static void WriteCmakeStub(string dir, string ranMarker, bool serverSmokeFails = false, string? argsDumpPath = null)
    {
        var versionLine = serverSmokeFails ? "exit 1" : "echo 'version: built'; exit 0";
        var dumpArgs = argsDumpPath is null ? string.Empty : $"echo \"$@\" >> '{argsDumpPath}'\n";
        var script = "#!/bin/sh\n"
                     + $"touch '{ranMarker}'\n"
                     + dumpArgs
                     + "if [ \"$1\" = \"-B\" ]; then\n"
                     + "  mkdir -p \"$2\"\n"
                     + "  exit 0\n"
                     + "fi\n"
                     + "if [ \"$1\" = \"--build\" ]; then\n"
                     + "  BIN=\"$2/bin\"\n"
                     + "  mkdir -p \"$BIN\"\n"
                     + "  cat > \"$BIN/llama-server\" <<'EOF'\n"
                     + "#!/bin/sh\n"
                     + "case \"$1\" in\n"
                     + $"  --version) {versionLine} ;;\n"
                     + "  --list-devices) echo '  CUDA0: Test GPU (24000 MiB, 23000 MiB free)'; exit 0 ;;\n"
                     + "  *) exit 0 ;;\n"
                     + "esac\n"
                     + "EOF\n"
                     + "  chmod 0755 \"$BIN/llama-server\"\n"
                     + "  printf '#!/bin/sh\\nexit 0\\n' > \"$BIN/llama-fit-params\"\n"
                     + "  chmod 0755 \"$BIN/llama-fit-params\"\n"
                     + "  exit 0\n"
                     + "fi\n"
                     + "exit 0\n";
        WriteScript(Path.Combine(dir, "cmake"), script);
    }

    [UnsupportedOSPlatform("windows")]
    private static void WriteScript(string path, string content)
    {
        File.WriteAllText(path, content);
        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    private static PathRestore PrependPath(string stubDir)
    {
        var original = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        Environment.SetEnvironmentVariable("PATH", stubDir + Path.PathSeparator + original);
        return new PathRestore(original);
    }

    private static PathRestore SetExactPath(string stubDir)
    {
        var original = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        Environment.SetEnvironmentVariable("PATH", stubDir);
        return new PathRestore(original);
    }

    private sealed class PathRestore(string original) : IDisposable
    {
        public void Dispose()
        {
            Environment.SetEnvironmentVariable("PATH", original);
        }
    }

    private sealed class AlwaysBuildableProbe : ICudaBuildPrerequisiteProbe
    {
        public Task<CudaBuildPrerequisiteReport> ProbeAsync(CancellationToken ct)
        {
            return Task.FromResult(new CudaBuildPrerequisiteReport(CanBuild: true,
                [new CudaBuildPrerequisiteItem("os-is-linux", Satisfied: true, "ok")]));
        }
    }

    private sealed class StubVendorProbe(DetectedGpuVendor vendor) : IGpuVendorProbe
    {
        public Task<DetectedGpuVendor> DetectVendorAsync(CancellationToken ct)
        {
            return Task.FromResult(vendor);
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("No network call expected during an in-app build.");
        }
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xe-cuda-build-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(Path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
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
