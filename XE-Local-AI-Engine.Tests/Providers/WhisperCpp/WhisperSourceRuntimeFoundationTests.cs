namespace XE_Local_AI_Engine.Tests.Providers.WhisperCpp;

using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.WhisperCpp;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;
using XE_Local_AI_Engine.Providers.WhisperCpp.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

/// <summary>
///     The foundations the managed Linux CUDA source-build lane stands on: the scrubbed child environment, the
///     prerequisite checklist, and the journaled adoption transaction.
/// </summary>
/// <remarks>
///     Adoption is driven directly rather than through the build service, because these are properties of the
///     transaction itself and proving them here keeps them provable without compiling anything native.
/// </remarks>
[Category(TestCategories.Integration)]
public sealed class WhisperSourceRuntimeFoundationTests
{
    [Test]
    public void Hardening_ClosesStdinAndScrubsTheEnvironment()
    {
        // A source build runs third-party build scripts with the app user's privileges, so the environment is cleared
        // rather than filtered: an inherited token must not reach a configure script, and every prompt channel git
        // has must be shut so a hidden password prompt cannot masquerade as a hung build.
        using var temp = new TempDirectory();
        var startInfo = new ProcessStartInfo();
        startInfo.Environment.Clear();
        startInfo.Environment["PATH"] = "/toolchain/bin";
        startInfo.Environment["LANG"] = "en_US.UTF-8";
        startInfo.Environment["LC_ALL"] = "en_US.UTF-8";
        startInfo.Environment["CUDA_HOME"] = "/opt/cuda-home";
        startInfo.Environment["CUDA_PATH"] = "/opt/cuda-path";
        startInfo.Environment["GITHUB_TOKEN"] = "poison";
        startInfo.Environment["HF_TOKEN"] = "poison";
        startInfo.Environment["GIT_CONFIG_COUNT"] = "1";

        WhisperSourceProcessHardening.Configure(startInfo, temp.Path);

        AssertEx.Equal("/toolchain/bin", startInfo.Environment["PATH"]);
        AssertEx.Equal("en_US.UTF-8", startInfo.Environment["LANG"]);
        AssertEx.Equal("en_US.UTF-8", startInfo.Environment["LC_ALL"]);
        AssertEx.Equal("/opt/cuda-home", startInfo.Environment["CUDA_HOME"]);
        AssertEx.Equal("/opt/cuda-path", startInfo.Environment["CUDA_PATH"]);
        AssertEx.False(startInfo.Environment.ContainsKey("GITHUB_TOKEN"), "An inherited token must never reach a build script.");
        AssertEx.False(startInfo.Environment.ContainsKey("HF_TOKEN"), "An inherited token must never reach a build script.");
        AssertEx.False(startInfo.Environment.ContainsKey("GIT_CONFIG_COUNT"));
        AssertEx.Equal("0", startInfo.Environment["GIT_TERMINAL_PROMPT"]);
        AssertEx.Equal("1", startInfo.Environment["GIT_CONFIG_NOSYSTEM"]);
        AssertEx.Equal("/bin/false", startInfo.Environment["GIT_ASKPASS"]);

        // RedirectStandardInput is what makes CloseStandardInput possible; a tool that asks a question then reads
        // end-of-file and fails instead of waiting forever.
        AssertEx.True(startInfo.RedirectStandardInput, "Standard input must be redirected so it can be closed.");
        AssertEx.True(Directory.Exists(startInfo.Environment["HOME"]), "The child gets its own HOME inside the isolation root.");
        AssertEx.True(Directory.Exists(startInfo.Environment["TMPDIR"]), "The child gets its own TMPDIR inside the isolation root.");
        AssertEx.True(startInfo.Environment["HOME"]!.StartsWith(temp.Path, StringComparison.Ordinal));
        AssertEx.True(startInfo.Environment["TMPDIR"]!.StartsWith(temp.Path, StringComparison.Ordinal));
    }

    [Test]
    [RunOn(OS.Windows)]
    public async Task PrerequisiteProbe_NonLinux_ReportsOsGateOnly()
    {
        // The gate is a checklist row, not an exception, so the SPA renders one shape everywhere and a Windows
        // operator is told why the lane is unavailable rather than shown an error.
        using var temp = new TempDirectory();
        var probe = new WhisperCppSourceBuildPrerequisiteProbe(temp.Path);

        var report = await probe.ProbeAsync(WhisperBackend.Cuda, CancellationToken.None);

        AssertEx.False(report.CanBuild);
        AssertEx.Equal(expected: 1, report.Items.Count, "A non-Linux host reports the OS gate and nothing else.");
        AssertEx.Equal("os-is-linux", report.Items[0].Key);
        AssertEx.False(report.Items[0].Satisfied);
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task PrerequisiteProbe_CudaBackend_IncludesNvccAndNvidiaSmi()
    {
        // The CUDA rows are what make a refusal actionable: without them the operator is told only that "one or more
        // prerequisites are missing". Satisfaction depends on this box's toolchain, so only the checklist SHAPE is
        // asserted — the rows must be present and the CPU backend must not carry them.
        using var temp = new TempDirectory();
        var probe = new WhisperCppSourceBuildPrerequisiteProbe(temp.Path);

        var cuda = await probe.ProbeAsync(WhisperBackend.Cuda, CancellationToken.None);
        var cpu = await probe.ProbeAsync(WhisperBackend.Cpu, CancellationToken.None);

        var cudaKeys = cuda.Items.Select(static item => item.Key).ToList();
        AssertEx.True(cudaKeys.Contains("nvcc"), "A CUDA build needs the CUDA compiler and must say so by name.");
        AssertEx.True(cudaKeys.Contains("nvidia-smi"), "A CUDA build needs a working driver and must say so by name.");

        var cpuKeys = cpu.Items.Select(static item => item.Key).ToList();
        AssertEx.False(cpuKeys.Contains("nvcc"), "A CPU build must not demand the CUDA toolchain.");
        AssertEx.False(cpuKeys.Contains("nvidia-smi"), "A CPU build must not demand an NVIDIA driver.");

        // Both backends share the toolchain floor, and the either-or build driver is one row rather than two, so a
        // box that builds fine with make is not failed for lacking ninja.
        // readelf is on the floor because the post-build relocation gate runs it: without binutils a build compiles
        // for up to two hours and then fails a check the checklist could have surfaced up front.
        foreach (var key in new[]
                 {
                     "os-is-linux",
                     "cmake",
                     "gcc",
                     "g++",
                     "git",
                     "readelf",
                     "make-or-ninja",
                     "free-disk"
                 })
        {
            AssertEx.True(cpuKeys.Contains(key), $"The checklist must carry the '{key}' row.");
            AssertEx.True(cudaKeys.Contains(key), $"The checklist must carry the '{key}' row.");
        }

        AssertEx.False(cpuKeys.Contains("ninja"), "Ninja and Make share one row; neither may appear under its own key.");
        AssertEx.False(cpuKeys.Contains("make"), "Ninja and Make share one row; neither may appear under its own key.");
    }

    /// <summary>
    ///     The free-disk row must describe the mount the cache root actually sits on. It measured the path's ROOT
    ///     instead, which on Linux is <c>/</c> for every absolute path there is, so a cache on a redirected data
    ///     volume was gated on the root filesystem's free space.
    /// </summary>
    [Test]
    [RunOn(OS.Linux)]
    public async Task PrerequisiteProbe_FreeDisk_MeasuresTheMountHoldingTheCacheRootRatherThanTheRootFilesystem()
    {
        using var scratch = SeparateMountScratch.CreateOrSkip("xe-whisper-source-prereq-disk");
        var probe = new WhisperCppSourceBuildPrerequisiteProbe(scratch.Path, scratch.ThresholdBetweenBytes);

        var report = await probe.ProbeAsync(WhisperBackend.Cpu, CancellationToken.None);

        var freeDisk = report.Items.Single(static item => item.Key == "free-disk");
        AssertEx.Equal(scratch.ExpectedSatisfied, freeDisk.Satisfied, scratch.WrongMountMessage);
    }

    [Test]
    [RunOn(OS.Linux)]
    [UnsupportedOSPlatform("windows")]
    public async Task Adoption_RollsBackOnAValidationFailure()
    {
        // The record write is the last step, so a failure there is the sharpest test of the rollback: the new tree is
        // already in place and must be taken back out, with the previous runtime and its record restored exactly.
        using var cache = new TempDirectory();
        using var inner = new WhisperInstalledRuntimeStore(cache.Path);
        var store = new FailingWriteStore(inner);
        var signal = new WhisperManagedSourceBuildSignal();
        var adoption = new WhisperCppRuntimeAdoption(cache.Path, store, signal, TimeProvider.System, NullLogger.Instance);

        var previousCommit = new string(c: 'a', count: 40);
        var previousRoot = Path.Combine(cache.Path, "whisper.cpp", "managed", "cuda", previousCommit);
        Directory.CreateDirectory(previousRoot);
        await File.WriteAllTextAsync(Path.Combine(previousRoot, "whisper-server"), "previous");
        var previousState = State(previousCommit, previousRoot, Sha("previous"));
        await store.Inner.WriteAsync(previousState, CancellationToken.None);
        signal.SetActive(WhisperBackend.Cuda);

        var newCommit = new string(c: 'b', count: 40);
        var buildDir = BuildTree(cache.Path, "fresh");
        var descriptor = Descriptor(newCommit);

        store.FailNextWrite = true;
        _ = await AssertEx.ThrowsAsync<IOException>(() =>
            adoption.AdoptAsync(buildDir, Path.Combine(buildDir, "bin", "whisper-server"), descriptor, CancellationToken.None));

        var restored = AssertEx.NotNull(await store.Inner.ReadAsync(CancellationToken.None));
        AssertEx.Equal(previousCommit, restored.SourceCommit);
        AssertEx.Equal(WhisperInstalledRuntimeValidity.Active, restored.Validity);
        AssertEx.True(Directory.Exists(previousRoot), "The previous managed runtime directory must be back where it was.");
        AssertEx.Equal("previous", await File.ReadAllTextAsync(Path.Combine(previousRoot, "whisper-server")));
        AssertEx.False(Directory.Exists(Path.Combine(cache.Path, "whisper.cpp", "managed", "cuda", newCommit)),
            "The failed adoption must leave no runtime at the new commit.");
        AssertEx.False(File.Exists(Path.Combine(cache.Path, "whisper.cpp", "source-build", "adoption-journal.json")),
            "A completed rollback drops its journal; a retained one would be replayed on the next start.");
    }

    [Test]
    [RunOn(OS.Linux)]
    [UnsupportedOSPlatform("windows")]
    public async Task Adoption_RecoversAnInterruptedJournal()
    {
        // The host died after the journal was written but before the swap. Recovery must put the previous runtime's
        // record back and drop the journal, rather than leaving an adoption half-declared forever.
        using var cache = new TempDirectory();
        using var store = new WhisperInstalledRuntimeStore(cache.Path);
        var signal = new WhisperManagedSourceBuildSignal();
        var adoption = new WhisperCppRuntimeAdoption(cache.Path, store, signal, TimeProvider.System, NullLogger.Instance);

        var previousCommit = new string(c: 'c', count: 40);
        var previousRoot = Path.Combine(cache.Path, "whisper.cpp", "managed", "cuda", previousCommit);
        Directory.CreateDirectory(previousRoot);
        await File.WriteAllTextAsync(Path.Combine(previousRoot, "whisper-server"), "previous");
        var previousState = State(previousCommit, previousRoot, Sha("previous"));
        await store.WriteAsync(previousState, CancellationToken.None);

        var newCommit = new string(c: 'd', count: 40);
        var newRoot = Path.Combine(cache.Path, "whisper.cpp", "managed", "cuda", newCommit);
        var journal = new WhisperCppAdoptionJournal(Guid.NewGuid(),
            WhisperBackend.Cuda,
            newCommit,
            HadPreviousDestination: false,
            previousState,
            State(newCommit, newRoot, Sha("fresh")));
        var buildRoot = Path.Combine(cache.Path, "whisper.cpp", "source-build");
        Directory.CreateDirectory(buildRoot);
        var journalPath = Path.Combine(buildRoot, "adoption-journal.json");
        await File.WriteAllTextAsync(journalPath, JsonSerializer.Serialize(journal));

        // The signal starts clear, as it would on a cold host, so a recovery that silently left it set would be
        // visible here.
        signal.Clear();
        await adoption.RecoverAsync(CancellationToken.None);

        var reconciled = AssertEx.NotNull(await store.ReadAsync(CancellationToken.None));
        AssertEx.Equal(previousCommit, reconciled.SourceCommit);
        AssertEx.True(Directory.Exists(previousRoot), "The untouched previous runtime must survive recovery.");
        AssertEx.False(File.Exists(journalPath), "A reconciled journal must be removed, or it is replayed forever.");
        AssertEx.Equal(WhisperBackend.Cuda, signal.ActiveBackend);
    }

    // Custom + ExplicitCommit rather than Official + EnginePinned: the runtime store enforces that an engine-pinned
    // record carries WhisperCppReleasePins.PinnedSourceCommitSha exactly, so an official-source fixture could only
    // ever use one commit — and these tests need two distinct ones to tell a rollback from a no-op.
    private static WhisperCppSourceBuildDescriptor Descriptor(string resolvedCommit)
    {
        return new WhisperCppSourceBuildDescriptor(WhisperBackend.Cuda,
            WhisperCppSourceSelection.Custom,
            WhisperCppSourceBuildRequestValidation.OfficialRepository,
            WhisperCppSourceRevisionMode.ExplicitCommit,
            resolvedCommit,
            resolvedCommit)
        {
            BuildId = Guid.NewGuid()
        };
    }

    private static WhisperInstalledRuntimeState State(string commit, string installRoot, string sha)
    {
        return new WhisperInstalledRuntimeState(WhisperInstalledRuntimeValidity.Active,
            WhisperBackend.Cuda,
            WhisperCppSourceBuildRequestValidation.OfficialRepository,
            commit,
            WhisperCppSourceSelection.Custom,
            WhisperCppSourceRevisionMode.ExplicitCommit,
            commit,
            installRoot,
            sha,
            DateTimeOffset.UtcNow);
    }

    // A build output shaped the way adoption expects: the server under bin/, executable, beside a sibling library.
    [UnsupportedOSPlatform("windows")]
    private static string BuildTree(string cacheRoot, string marker)
    {
        var buildDir = Path.Combine(cacheRoot, "work", "build-" + Guid.NewGuid().ToString("N"));
        var bin = Path.Combine(buildDir, "bin");
        Directory.CreateDirectory(bin);
        var server = Path.Combine(bin, "whisper-server");
        File.WriteAllText(server, marker);
        File.WriteAllText(Path.Combine(bin, "libwhisper.so.1.9.4"), "library");
        File.SetUnixFileMode(server, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return buildDir;
    }

    private static string Sha(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    /// <summary>Fails exactly one record write, so the adoption's last step is the one that breaks.</summary>
    private sealed class FailingWriteStore(WhisperInstalledRuntimeStore inner) : IWhisperInstalledRuntimeStore
    {
        public WhisperInstalledRuntimeStore Inner { get; } = inner;

        public bool FailNextWrite { get; set; }

        public Task<WhisperInstalledRuntimeState?> ReadAsync(CancellationToken ct) =>
            Inner.ReadAsync(ct);

        public Task WriteAsync(WhisperInstalledRuntimeState state, CancellationToken ct)
        {
            if (FailNextWrite)
            {
                FailNextWrite = false;
                throw new IOException("The managed runtime record could not be written.");
            }

            return Inner.WriteAsync(state, ct);
        }

        public Task DeleteAsync(CancellationToken ct) =>
            Inner.DeleteAsync(ct);
    }
}
