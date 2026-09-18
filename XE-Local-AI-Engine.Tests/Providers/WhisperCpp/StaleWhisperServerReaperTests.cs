namespace XE_Local_AI_Engine.Tests.Providers.WhisperCpp;

using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;
using XE_Local_AI_Engine.Providers.WhisperCpp.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The startup reaper must kill a previous run's orphan and nothing else. All matching runs through an in-memory
///     scanner fake: no real process, and no real file I/O, because the path filter is pure string normalization.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class StaleWhisperServerReaperTests
{
    private static readonly string BinariesRoot = Path.Combine(Path.GetTempPath(), "xe-whisper-reaper-test", "whisper.cpp");

    [Test]
    public async Task Reap_KillsOnlyProcessesUnderOurBinariesRoot()
    {
        var ours = Path.Combine(BinariesRoot, "b5130", "cpu", "whisper-bin-ubuntu-x64", "whisper-server");
        var foreign = OperatingSystem.IsWindows()
            ? @"C:\Program Files\SomeOtherApp\whisper-server.exe"
            : "/opt/some-other-app/whisper-server";
        var scanner = new FakeStaleWhisperServerProcessScanner([
            new StaleWhisperServerProcess(4242, ours),
            new StaleWhisperServerProcess(4243, foreign)
        ]);
        var reaper = new StaleWhisperServerReaper(scanner, BinariesRoot, NullLogger<StaleWhisperServerReaper>.Instance);

        await reaper.StartAsync(CancellationToken.None);

        // An operator's own whisper-server build is a completely normal thing to have running. Killing it would be a
        // far worse failure than leaving one of ours behind.
        AssertEx.Equal(expected: 1, scanner.KilledPids.Count);
        AssertEx.Equal(expected: 4242, scanner.KilledPids[0]);
    }

    [Test]
    public async Task Reap_SiblingPrefixDirectory_IsNotReaped()
    {
        // ".../whisper.cpp-other/..." shares a string prefix with the root but is NOT under it.
        var siblingPrefix = Path.Combine(Path.GetTempPath(), "xe-whisper-reaper-test", "whisper.cpp-other", "whisper-server");
        var scanner = new FakeStaleWhisperServerProcessScanner([new StaleWhisperServerProcess(7, siblingPrefix)]);
        var reaper = new StaleWhisperServerReaper(scanner, BinariesRoot, NullLogger<StaleWhisperServerReaper>.Instance);

        await reaper.StartAsync(CancellationToken.None);

        AssertEx.Empty(scanner.KilledPids);
    }

    [Test]
    public async Task Reap_CandidateWithNoResolvedPath_IsNotReaped()
    {
        // A process whose executable path could not be read cannot be proven to be ours, so it is left alone.
        var scanner = new FakeStaleWhisperServerProcessScanner([new StaleWhisperServerProcess(9, ExecutablePath: null)]);
        var reaper = new StaleWhisperServerReaper(scanner, BinariesRoot, NullLogger<StaleWhisperServerReaper>.Instance);

        await reaper.StartAsync(CancellationToken.None);

        AssertEx.Empty(scanner.KilledPids);
    }

    [Test]
    public async Task Reap_WithNoBinariesRoot_IsANoOp()
    {
        var scanner = new FakeStaleWhisperServerProcessScanner([new StaleWhisperServerProcess(11, "/anything/whisper-server")]);
        var reaper = new StaleWhisperServerReaper(scanner, binariesRoot: null, NullLogger<StaleWhisperServerReaper>.Instance);

        await reaper.StartAsync(CancellationToken.None);

        AssertEx.Empty(scanner.KilledPids);
        AssertEx.Equal(expected: 0, scanner.EnumerateCallCount, "An unresolvable root must not even scan.");
    }

    [Test]
    public async Task Reap_ScannerThrows_DoesNotBlockStartup()
    {
        // A reaper failure must never stop the application from starting.
        var scanner = new ThrowingStaleWhisperServerProcessScanner();
        var reaper = new StaleWhisperServerReaper(scanner, BinariesRoot, NullLogger<StaleWhisperServerReaper>.Instance);

        var start = reaper.StartAsync(CancellationToken.None);
        await start;

        AssertEx.True(start.IsCompletedSuccessfully,
            "A failing process scan must be swallowed so host startup continues.");
        AssertEx.Equal(expected: 1, scanner.EnumerateCallCount,
            "The scan must actually have been attempted, or this proves nothing about swallowing its failure.");
    }

    [Test]
    public async Task StopAsync_IsANoOp()
    {
        var scanner = new FakeStaleWhisperServerProcessScanner([]);
        var reaper = new StaleWhisperServerReaper(scanner, BinariesRoot, NullLogger<StaleWhisperServerReaper>.Instance);

        await reaper.StopAsync(CancellationToken.None);

        AssertEx.Empty(scanner.KilledPids);
    }

    private sealed class FakeStaleWhisperServerProcessScanner : IStaleWhisperServerProcessScanner
    {
        private readonly IReadOnlyList<StaleWhisperServerProcess> _candidates;

        public FakeStaleWhisperServerProcessScanner(IReadOnlyList<StaleWhisperServerProcess> candidates)
        {
            _candidates = candidates;
        }

        public List<int> KilledPids { get; } = [];

        public int EnumerateCallCount { get; private set; }

        public IReadOnlyList<StaleWhisperServerProcess> EnumerateWhisperServerProcesses()
        {
            EnumerateCallCount++;
            return _candidates;
        }

        public void KillProcessTree(int pid) =>
            KilledPids.Add(pid);
    }

    private sealed class ThrowingStaleWhisperServerProcessScanner : IStaleWhisperServerProcessScanner
    {
        public int EnumerateCallCount { get; private set; }

        public IReadOnlyList<StaleWhisperServerProcess> EnumerateWhisperServerProcesses()
        {
            EnumerateCallCount++;
            throw new InvalidOperationException("The process table could not be read.");
        }

        public void KillProcessTree(int pid) =>
            throw new InvalidOperationException("The scan failed, so no kill can follow.");
    }
}
