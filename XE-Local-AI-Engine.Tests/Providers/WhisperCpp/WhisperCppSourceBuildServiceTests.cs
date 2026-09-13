namespace XE_Local_AI_Engine.Tests.Providers.WhisperCpp;

using System.Runtime.Versioning;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.WhisperCpp;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;
using XE_Local_AI_Engine.Providers.WhisperCpp.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

/// <summary>
///     The managed source build's refusals, its cmake contract, and the relocation gate that stands between a
///     finished build and adoption.
/// </summary>
/// <remarks>
///     The command runner is faked throughout: this suite must never compile a native runtime. What it pins is the
///     decisions the service makes around those commands — when it refuses to start, what it asks cmake for, and what
///     it does with what <c>readelf</c> reports.
/// </remarks>
public sealed class WhisperCppSourceBuildServiceTests
{
    [Test]
    public async Task Start_NonLinux_Throws()
    {
        // The lane is Linux-only, and saying so loudly beats letting a Windows node discover it three commands in.
        using var cache = new TempDirectory();
        using var harness = new Harness(cache.Path, isLinux: false);

        var exception = await AssertEx.ThrowsAsync<WhisperRuntimeException>(() =>
            harness.Service.StartAsync(OfficialCudaRequest, CancellationToken.None));

        AssertEx.Equal("In-app source builds are available on Linux only.", exception.Message);
        AssertEx.Equal(expected: 0, harness.Runner.Invocations.Count, "Nothing may be executed on a host that cannot build.");
    }

    [Test]
    public async Task Start_WhileTheRuntimeIsBusy_ReportsRuntimeBusyWithTheSnapshot()
    {
        // A build replaces the tree the daemon runs from, so it may not start while a transcription is in flight.
        // This is the 409 the operator sees, and the snapshot is what lets the UI say WHAT to wait for.
        using var cache = new TempDirectory();
        using var harness = new Harness(cache.Path);
        using var transcription = harness.ActivityGate.TryAcquireTranscriptionLease();
        AssertEx.NotNull(transcription);

        var result = await harness.Service.StartAsync(OfficialCudaRequest, CancellationToken.None);

        AssertEx.Equal(WhisperCppSourceBuildStartOutcome.RuntimeBusy, result.Outcome);
        var activity = AssertEx.NotNull(result.Activity);
        AssertEx.Equal(expected: 1, activity.ActiveTranscriptionCount);
        AssertEx.True(activity.IsBusy);
        AssertEx.Equal(expected: 0, harness.Runner.Invocations.Count, "A refused start must not run a single command.");
    }

    [Test]
    public async Task Start_AlreadyRunning_ReportsAlreadyRunning()
    {
        // Single-flight: a double submit rejoins the running build instead of racing a second one onto the same tree.
        using var cache = new TempDirectory();
        using var harness = new Harness(cache.Path);
        harness.Runner.BlockOn = "cmake";

        var first = await harness.Service.StartAsync(OfficialCudaRequest, CancellationToken.None);
        AssertEx.Equal(WhisperCppSourceBuildStartOutcome.Started, first.Outcome);
        await AssertEx.EventuallyAsync(() => harness.Service.GetStatus().IsRunning, TestBudgets.Contended);

        var second = await harness.Service.StartAsync(OfficialCudaRequest, CancellationToken.None);

        AssertEx.Equal(WhisperCppSourceBuildStartOutcome.AlreadyRunning, second.Outcome);
        AssertEx.True(harness.Service.Cancel(), "A running build must be cancellable.");
        harness.Runner.Release();
        await AssertEx.EventuallyAsync(() => harness.Service.GetStatus().Terminal, TestBudgets.Contended);
    }

    [Test]
    [RunOn(OS.Linux)]
    [UnsupportedOSPlatform("windows")]
    public async Task Remove_WhileTheRuntimeIsBusy_ReportsRuntimeBusy()
    {
        // Removing the managed runtime is the same mutation as replacing it, so it takes the same reservation.
        using var cache = new TempDirectory();
        using var harness = new Harness(cache.Path);
        using var resident = harness.ActivityGate.TryAcquireResidentProcessLease();
        AssertEx.NotNull(resident);

        var result = await harness.Service.RemoveAsync(CancellationToken.None);

        AssertEx.Equal(WhisperCppSourceBuildRemoveOutcome.RuntimeBusy, result.Outcome);
        var activity = AssertEx.NotNull(result.Activity);
        AssertEx.Equal(expected: 1, activity.ResidentProcessCount);
    }

    [Test]
    public void BuildCMakeConfigureArguments_Cuda_EmitsGgmlCudaOnAndTheOriginRpathTriple()
    {
        // The three rpath arguments are what make the produced binary survive adoption's move. $ORIGIN must arrive as
        // the bare literal: there is no shell in this path, so an escaped or quoted value would be written into the
        // binary verbatim and resolve to nothing.
        var arguments = WhisperCppSourceBuildService.BuildCMakeConfigureArguments("/src", "/build", WhisperBackend.Cuda, "120");

        AssertEx.Contains(arguments, "-DCMAKE_BUILD_RPATH_USE_ORIGIN=ON");
        AssertEx.Contains(arguments, "-DCMAKE_BUILD_WITH_INSTALL_RPATH=ON");
        AssertEx.Contains(arguments, "-DCMAKE_INSTALL_RPATH=$ORIGIN");
        AssertEx.Contains(arguments, "-DGGML_CUDA=ON");
        AssertEx.Contains(arguments, "-DCMAKE_CUDA_ARCHITECTURES=120");

        var installRpath = arguments.Single(static argument => argument.StartsWith("-DCMAKE_INSTALL_RPATH=", StringComparison.Ordinal));
        AssertEx.Equal("-DCMAKE_INSTALL_RPATH=$ORIGIN", installRpath);
        AssertEx.False(installRpath.Contains('\\', StringComparison.Ordinal), "The $ORIGIN literal must not be shell-escaped.");
        AssertEx.False(installRpath.Contains('"', StringComparison.Ordinal), "The $ORIGIN literal must not be quoted.");
        AssertEx.False(installRpath.Contains('\'', StringComparison.Ordinal), "The $ORIGIN literal must not be quoted.");
    }

    [Test]
    public void BuildCMakeConfigureArguments_Cpu_EmitsGgmlCudaOff()
    {
        var arguments = WhisperCppSourceBuildService.BuildCMakeConfigureArguments("/src", "/build", WhisperBackend.Cpu);

        AssertEx.Contains(arguments, "-DGGML_CUDA=OFF");
        AssertEx.False(arguments.Any(static argument => argument.StartsWith("-DCMAKE_CUDA_ARCHITECTURES=", StringComparison.Ordinal)),
            "A CPU build must not pin CUDA architectures.");

        // The rpath triple is backend-independent: a CPU build is relocated by the same adoption move.
        AssertEx.Contains(arguments, "-DCMAKE_INSTALL_RPATH=$ORIGIN");
    }

    [Test]
    public void ValidateRequestedBackendArtifacts_MissingCudaArtifact_Throws()
    {
        // The CMake cache says what was ASKED for; a named artifact is what proves the toolchain produced it.
        using var build = new TempDirectory();
        File.WriteAllText(Path.Combine(build.Path, "CMakeCache.txt"), "GGML_CUDA:BOOL=ON\nCMAKE_BUILD_TYPE:STRING=Release\n");
        File.WriteAllText(Path.Combine(build.Path, "libggml-cpu.so"), "not the cuda backend");

        var exception = AssertEx.Throws<WhisperRuntimeException>(() =>
            WhisperCppSourceBuildService.ValidateRequestedBackendArtifacts(build.Path, WhisperBackend.Cuda));

        AssertEx.Equal("The source build did not produce the requested cuda backend artifact.", exception.Message);

        File.WriteAllText(Path.Combine(build.Path, "libggml-cuda.so"), "the cuda backend");
        AssertEx.DoesNotThrow(() => WhisperCppSourceBuildService.ValidateRequestedBackendArtifacts(build.Path, WhisperBackend.Cuda),
            "A build carrying the cuda artifact must pass.");
    }

    [Test]
    public void ValidateRequestedBackendArtifacts_CacheDisagreesWithTheRequest_Throws()
    {
        using var build = new TempDirectory();
        File.WriteAllText(Path.Combine(build.Path, "CMakeCache.txt"), "GGML_CUDA:BOOL=OFF\n");

        var exception = AssertEx.Throws<WhisperRuntimeException>(() =>
            WhisperCppSourceBuildService.ValidateRequestedBackendArtifacts(build.Path, WhisperBackend.Cuda));

        AssertEx.Equal("The source build did not enable the requested cuda backend.", exception.Message);
    }

    [Test]
    [Arguments("Library runpath: [$ORIGIN:]")]
    [Arguments("Library runpath: [$ORIGIN]")]
    [Arguments("Library rpath: [$ORIGIN/../lib:$ORIGIN]")]
    public void PostBuild_OriginRunpath_Passes(string entry)
    {
        AssertEx.DoesNotThrow(() => WhisperCppSourceBuildService.ValidateRelocatableRunpath(Readelf(entry)),
            "An $ORIGIN-relative runpath is exactly what adoption's move depends on.");
    }

    [Test]
    [Arguments("Library runpath: [/tmp/work/build/bin]", "an absolute runpath")]
    [Arguments("Library rpath: [/usr/local/lib]", "an absolute rpath")]
    [Arguments("Library runpath: [$ORIGIN:/tmp/work/build/bin]", "one absolute element among relative ones")]
    public void PostBuild_AbsoluteRunpath_IsRejected(string entry, string because)
    {
        var exception = AssertEx.Throws<WhisperRuntimeException>(() =>
            WhisperCppSourceBuildService.ValidateRelocatableRunpath(Readelf(entry)));

        AssertEx.Equal("The source build produced a runtime that cannot be relocated.", exception.Message);
        AssertEx.NotEmpty(because);
    }

    [Test]
    public void PostBuild_MissingRunpath_IsRejected()
    {
        // No RUNPATH at all is the stock cmake outcome this gate exists to catch: the binary would resolve its
        // libraries only from the directory it was built in, which adoption is about to delete.
        var exception = AssertEx.Throws<WhisperRuntimeException>(() =>
            WhisperCppSourceBuildService.ValidateRelocatableRunpath("Dynamic section at offset 0x1000 contains 20 entries:\n"
                                                                    + " 0x0000000000000001 (NEEDED) Shared library: [libwhisper.so.1]\n"));

        AssertEx.Equal("The source build produced a runtime that cannot be relocated.", exception.Message);
    }

    [Test]
    [RunOn(OS.Linux)]
    [UnsupportedOSPlatform("windows")]
    public async Task PostBuild_AbsoluteRunpath_FailsBeforeAdoption()
    {
        // The gate's POSITION is the property, and the parser tests above cannot see it: moving
        // ValidateRelocatableRunpathAsync below AdoptAsync would keep every one of them green. Driven end to end
        // through StartAsync, the absence of a record and of a managed directory is what proves adoption was never
        // reached.
        using var cache = new TempDirectory();
        using var harness = new Harness(cache.Path);
        harness.Runner.Scripts["cmake"] = MaterializeBuildTree;
        harness.Runner.StandardOutput["readelf"] = Readelf("Library runpath: [/tmp/work/build/bin]");

        var started = await harness.Service.StartAsync(OfficialCudaRequest, CancellationToken.None);

        AssertEx.Equal(WhisperCppSourceBuildStartOutcome.Started, started.Outcome);
        await AssertEx.EventuallyAsync(() => harness.Service.GetStatus().Terminal, TestBudgets.Contended);
        var status = harness.Service.GetStatus();
        AssertEx.Equal(WhisperCppSourceBuildPhase.Failed, status.Phase);
        AssertEx.Contains(harness.Runner.Invocations,
            static invocation => invocation.StartsWith("readelf ", StringComparison.Ordinal),
            "The build must have reached the relocation gate rather than failing earlier.");
        AssertEx.False(harness.Runner.Invocations.Any(static invocation =>
                invocation.StartsWith("whisper-server ", StringComparison.Ordinal)),
            "The smoke test sits between the gate and adoption, so it must not have run.");
        AssertEx.Null(await harness.Store.ReadAsync(CancellationToken.None),
            "A rejected build must leave no installed-runtime record behind.");
        AssertEx.False(Directory.Exists(Path.Combine(cache.Path, "whisper.cpp", "managed")),
            "Adoption creates the managed install root, so its absence is what proves adoption was never reached.");
    }

    [Test]
    [RunOn(OS.Linux)]
    [UnsupportedOSPlatform("windows")]
    public async Task PostBuild_OriginRunpath_ReachesAdoption()
    {
        // The positive control for the test above: with the same scripted tree and an $ORIGIN-relative runpath the
        // build runs all the way through adoption, so a "fails before adoption" result cannot be an artefact of the
        // harness never getting that far.
        using var cache = new TempDirectory();
        using var harness = new Harness(cache.Path);
        harness.Runner.Scripts["cmake"] = MaterializeBuildTree;
        harness.Runner.StandardOutput["readelf"] = Readelf("Library runpath: [$ORIGIN]");

        var started = await harness.Service.StartAsync(OfficialCudaRequest, CancellationToken.None);

        AssertEx.Equal(WhisperCppSourceBuildStartOutcome.Started, started.Outcome);
        await AssertEx.EventuallyAsync(() => harness.Service.GetStatus().Terminal, TestBudgets.Contended);
        var status = harness.Service.GetStatus();
        AssertEx.Equal(WhisperCppSourceBuildPhase.Completed, status.Phase, status.SanitizedError ?? "The build must complete.");
        var installed = AssertEx.NotNull(await harness.Store.ReadAsync(CancellationToken.None),
            "An adopted build writes the installed-runtime record.");
        AssertEx.Equal(WhisperInstalledRuntimeValidity.Active, installed.Validity);
        AssertEx.Equal(WhisperBackend.Cuda, installed.DesiredBackend);
        AssertEx.True(Directory.Exists(Path.Combine(cache.Path, "whisper.cpp", "managed", "cuda", WhisperCppReleasePins.PinnedSourceCommitSha)),
            "The adopted tree must land under the managed install root, keyed by the resolved commit.");
    }

    [Test]
    [Arguments("12.0", "120")]
    [Arguments("8.9", "89")]
    [Arguments("8.6\n8.9", "86;89")]
    [Arguments("", "75;86;89;120")]
    [Arguments("not a capability", "75;86;89;120")]
    [Arguments("99.9", "75;86;89;120")]
    public void ParseCudaArchitectures_MapsComputeCapabilitiesAndFallsBackConservatively(string output, string expected)
    {
        // An unrecognised or unsupported capability falls back to the conservative set rather than failing the build:
        // a driver probe that cannot answer is not a reason to refuse to compile.
        AssertEx.Equal(expected, WhisperCppSourceBuildService.ParseCudaArchitectures(output));
    }

    [Test]
    [RunOn(OS.Linux)]
    [UnsupportedOSPlatform("windows")]
    public async Task Status_LogLines_AreSanitizedAndBounded()
    {
        // The log is surfaced to the operator, so it must not carry the home directory, and it must not grow without
        // bound while a two-hour build talks.
        using var cache = new TempDirectory();
        using var harness = new Harness(cache.Path);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        harness.Runner.EmitLines =
        [
            $"configuring in {home}/secret-path",
            new string(c: 'x', count: 4000)
        ];
        harness.Runner.EmitCount = 600;

        _ = await harness.Service.StartAsync(OfficialCudaRequest, CancellationToken.None);
        await AssertEx.EventuallyAsync(() => harness.Service.GetStatus().Terminal, TestBudgets.Contended);

        var status = harness.Service.GetStatus();
        AssertEx.True(status.LogLines.Count <= 500, $"The retained log must stay bounded; found {status.LogLines.Count} lines.");
        AssertEx.True(status.LogStartSequence > 0, "A truncated log must say how many lines it dropped.");
        AssertEx.False(status.LogLines.Any(line => line.Contains(home, StringComparison.Ordinal)),
            "The operator's home directory must never appear in a surfaced log line.");
        AssertEx.False(status.LogLines.Any(static line => line.Length > 1000), "Every retained line must be capped.");
    }

    [Test]
    [RunOn(OS.Linux)]
    [UnsupportedOSPlatform("windows")]
    public async Task Status_CommandEcho_IsSanitized()
    {
        // The echoed command line is the one log entry the service writes itself, and cmake's -S/-B arguments are
        // absolute paths under the cache root — which in production sits inside the operator's home directory. The
        // cache root here is deliberately placed there too, because a temp-rooted harness cannot see this leak.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home) || !Directory.Exists(home))
        {
            Skip.Test("A resolvable user profile directory is required to prove home-path scrubbing.");
            return;
        }

        using var cache = new TempDirectory("xe-test-whisper-echo", home);
        using var harness = new Harness(cache.Path);

        _ = await harness.Service.StartAsync(OfficialCudaRequest, CancellationToken.None);
        await AssertEx.EventuallyAsync(() => harness.Service.GetStatus().Terminal, TestBudgets.Contended);

        var status = harness.Service.GetStatus();
        AssertEx.Contains(status.LogLines,
            static line => line.StartsWith("> cmake ", StringComparison.Ordinal),
            "The build must echo the cmake command line it ran.");
        AssertEx.False(status.LogLines.Any(line => line.Contains(home, StringComparison.Ordinal)),
            "The echoed command line must be scrubbed like every other surfaced line.");
    }

    private static WhisperCppSourceBuildRequest OfficialCudaRequest => new(WhisperBackend.Cuda, WhisperCppSourceSelection.Official);

    private static string Readelf(string entry) =>
        "Dynamic section at offset 0x2d18 contains 30 entries:\n"
        + "  Tag        Type                         Name/Value\n"
        + " 0x0000000000000001 (NEEDED)             Shared library: [libwhisper.so.1]\n"
        + $" 0x000000000000001d (RUNPATH)            {entry}\n";

    /// <summary>
    ///     Stands in for what <c>cmake --build</c> leaves behind — the server the gate reads, the cache the backend
    ///     check reads, and a named CUDA artifact — so a scripted build can reach the post-build steps. Nothing here
    ///     is a real binary: the gate's only input is readelf's output, which the runner scripts too.
    /// </summary>
    private static void MaterializeBuildTree(IReadOnlyList<string> arguments)
    {
        var index = arguments.ToList().IndexOf("--build");
        if (index < 0 || index + 1 >= arguments.Count)
        {
            // The configure invocation shares the file name and leaves nothing behind.
            return;
        }

        var buildDir = arguments[index + 1];
        var binDir = Path.Combine(buildDir, "bin");
        _ = Directory.CreateDirectory(binDir);
        File.WriteAllText(Path.Combine(binDir, "whisper-server"), "not a real binary");
        File.WriteAllText(Path.Combine(buildDir, "CMakeCache.txt"), "GGML_CUDA:BOOL=ON\nCMAKE_BUILD_TYPE:STRING=Release\n");
        File.WriteAllText(Path.Combine(buildDir, "libggml-cuda.so"), "the cuda backend");
    }

    private sealed class Harness : IDisposable
    {
        public Harness(string cacheRoot, bool isLinux = true)
        {
            ActivityGate = new WhisperRuntimeActivityGate();
            Store = new WhisperInstalledRuntimeStore(cacheRoot);
            Runner = new ScriptedSourceCommandRunner();
            Service = new WhisperCppSourceBuildService(new AlwaysSatisfiedProbe(),
                Store,
                new WhisperManagedSourceBuildSignal(),
                ActivityGate,
                new NullWhisperCppSourceBuildEventPublisher(),
                NullLogger<WhisperCppSourceBuildService>.Instance,
                cacheRoot,
                Runner,
                isLinux);
        }

        public WhisperRuntimeActivityGate ActivityGate { get; }

        public WhisperInstalledRuntimeStore Store { get; }

        public ScriptedSourceCommandRunner Runner { get; }

        public WhisperCppSourceBuildService Service { get; }

        public void Dispose()
        {
            Runner.Release();
            Service.Dispose();
            Store.Dispose();
            Runner.Dispose();
        }
    }

    /// <summary>
    ///     Succeeds every command, optionally blocking on one of them and optionally flooding the log, so the service's
    ///     own decisions are what the tests observe.
    /// </summary>
    private sealed class ScriptedSourceCommandRunner : IWhisperSourceCommandRunner, IDisposable
    {
        private readonly List<string> _invocations = [];
        private readonly Lock _gate = new();
        private readonly SemaphoreSlim _block = new(initialCount: 0, maxCount: 1);
        private int _released;

        public IReadOnlyList<string> Invocations
        {
            get
            {
                lock (_gate)
                {
                    return _invocations.ToArray();
                }
            }
        }

        /// <summary>File name to block on until <see cref="Release" />, so a test can observe a running build.</summary>
        public string? BlockOn { get; set; }

        /// <summary>
        ///     Per-command side effects, keyed by file name, run when that command is invoked. This is what lets a
        ///     scripted build leave a tree behind for the steps that follow it. Populated before the build starts.
        /// </summary>
        public Dictionary<string, Action<IReadOnlyList<string>>> Scripts { get; } = new(StringComparer.Ordinal);

        /// <summary>Per-command standard output, keyed by file name. Populated before the build starts.</summary>
        public Dictionary<string, string> StandardOutput { get; } = new(StringComparer.Ordinal);

        public IReadOnlyList<string> EmitLines { get; set; } = [];

        public int EmitCount { get; set; }

        public void Release()
        {
            if (Interlocked.Exchange(ref _released, value: 1) == 0)
            {
                _ = _block.Release();
            }
        }

        public void Dispose() =>
            _block.Dispose();

        public async Task<WhisperSourceCommandResult> RunAsync(string fileName,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            Action<string> onOutput,
            TimeSpan timeout,
            bool captureOutput,
            CancellationToken ct)
        {
            var name = Path.GetFileName(fileName);
            lock (_gate)
            {
                _invocations.Add($"{name} {string.Join(' ', arguments)}");
            }

            if (Scripts.TryGetValue(name, out var script))
            {
                script(arguments);
            }

            if (BlockOn is not null && string.Equals(name, BlockOn, StringComparison.Ordinal))
            {
                await _block.WaitAsync(ct).ConfigureAwait(false);
            }

            for (var round = 0; round < EmitCount; round++)
            {
                foreach (var line in EmitLines)
                {
                    onOutput(line);
                }
            }

            // A resolved commit is what the verify phase asserts against; the pinned one keeps the official path honest.
            string stdout;
            if (StandardOutput.TryGetValue(name, out var scripted))
            {
                stdout = scripted;
            }
            else if (string.Equals(name, "git", StringComparison.Ordinal) && arguments.Contains("rev-parse"))
            {
                stdout = WhisperCppReleasePins.PinnedSourceCommitSha;
            }
            else
            {
                stdout = string.Empty;
            }

            return new WhisperSourceCommandResult(ExitCode: 0, stdout, StandardError: string.Empty);
        }
    }

    private sealed class AlwaysSatisfiedProbe : IWhisperCppSourceBuildPrerequisiteProbe
    {
        public Task<WhisperCppSourceBuildPrerequisiteReport> ProbeAsync(WhisperBackend backend, CancellationToken ct)
        {
            return Task.FromResult(new WhisperCppSourceBuildPrerequisiteReport(true,
                [new WhisperCppSourceBuildPrerequisiteItem("os-is-linux", true, "Linux host detected.")]));
        }
    }
}
