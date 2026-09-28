namespace XE_Local_AI_Engine.Tests.Providers.ProcessSupervision;

using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.ProcessSupervision;
using XE_Local_AI_Engine.Providers.ProcessSupervision.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Unit tests for the startup <see cref="StaleProcessReaper" /> the llama-server, sd-server and whisper-server
///     providers share: it must reap a previous-run orphan whose binary lives under the provider's own cache root, leave
///     any unrelated server of the same name (e.g. Ollama's <c>llama-server</c>) untouched, and never throw out of
///     <c>StartAsync</c>. All matching logic is exercised through an in-memory scanner fake — no real processes and no
///     real file I/O (the path filter is pure string normalization).
/// </summary>
[Category(TestCategories.Unit)]
public sealed class StaleProcessReaperTests
{
    private const string ServerName = "llama-server";

    private static readonly string BinariesRoot = Path.Combine(Path.GetTempPath(), "xe-reaper-test", "llama.cpp");

    private static readonly string ForeignServer = OperatingSystem.IsWindows()
        ? @"C:\Program Files\Ollama\llama-server.exe"
        : "/usr/lib/ollama/llama-server";

    [Test]
    public async Task StartAsync_WhenOrphanUnderBinariesRoot_TreeKillsIt()
    {
        var ourServer = OurServerPath("b9700", "vulkan");
        var scanner = new FakeStaleProcessScanner([new StaleProcess(1234, ourServer)]);
        var reaper = CreateReaper(scanner, BinariesRoot);

        await reaper.StartAsync(CancellationToken.None);

        AssertEx.Equal(expected: 1, scanner.KilledPids.Count);
        AssertEx.Equal(expected: 1234, scanner.KilledPids[0]);
    }

    [Test]
    public async Task StartAsync_WhenServerOutsideRoot_DoesNotTreeKillIt()
    {
        // An unrelated llama-server (Ollama bundles one) must never be reaped.
        var scanner = new FakeStaleProcessScanner([new StaleProcess(4321, ForeignServer)]);
        var reaper = CreateReaper(scanner, BinariesRoot);

        await reaper.StartAsync(CancellationToken.None);

        AssertEx.Equal(expected: 0, scanner.KilledPids.Count);
    }

    [Test]
    public async Task StartAsync_KillsOnlyProcessesUnderOurBinariesRoot()
    {
        var scanner = new FakeStaleProcessScanner([
            new StaleProcess(4242, OurServerPath("b5130", "cpu")),
            new StaleProcess(4243, ForeignServer)
        ]);
        var reaper = CreateReaper(scanner, BinariesRoot);

        await reaper.StartAsync(CancellationToken.None);

        // An operator's own server build is a completely normal thing to have running. Killing it would be a far worse
        // failure than leaving one of ours behind.
        AssertEx.Equal(expected: 1, scanner.KilledPids.Count);
        AssertEx.Equal(expected: 4242, scanner.KilledPids[0]);
    }

    [Test]
    public async Task StartAsync_WhenSiblingPrefixPath_DoesNotTreeKillIt()
    {
        // ".../llama.cpp-other/..." shares a string prefix with the root ".../llama.cpp" but is NOT under it.
        var siblingPrefix = Path.Combine(Path.GetTempPath(), "xe-reaper-test", "llama.cpp-other", "llama-server");
        var scanner = new FakeStaleProcessScanner([new StaleProcess(7, siblingPrefix)]);
        var reaper = CreateReaper(scanner, BinariesRoot);

        await reaper.StartAsync(CancellationToken.None);

        AssertEx.Equal(expected: 0, scanner.KilledPids.Count);
    }

    [Test]
    public async Task StartAsync_WhenExecutablePathUnresolved_SkipsCandidate()
    {
        // A process whose executable path could not be read cannot be proven to be ours, so it is left alone.
        var scanner = new FakeStaleProcessScanner([new StaleProcess(99, ExecutablePath: null)]);
        var reaper = CreateReaper(scanner, BinariesRoot);

        await reaper.StartAsync(CancellationToken.None);

        AssertEx.Equal(expected: 0, scanner.KilledPids.Count);
    }

    [Test]
    public async Task StartAsync_WhenBinariesRootUnresolved_ReapsNothing()
    {
        var ourServer = OurServerPath("b9700", "cpu");
        var scanner = new FakeStaleProcessScanner([new StaleProcess(1234, ourServer)]);
        var reaper = CreateReaper(scanner, binariesRoot: null);

        await reaper.StartAsync(CancellationToken.None);

        // A null root disables the reap entirely — even a clearly-ours binary is left alone.
        AssertEx.Equal(expected: 0, scanner.EnumerateCallCount, "Scanner must not be consulted when the binaries root is unresolved.");
        AssertEx.Equal(expected: 0, scanner.KilledPids.Count);
    }

    [Test]
    public async Task StartAsync_WhenMultipleCandidates_ReapsOnlyThoseUnderRoot()
    {
        var first = OurServerPath("b9700", "vulkan");
        var second = OurServerPath("b9692", "cuda");

        var scanner = new FakeStaleProcessScanner([
            new StaleProcess(1, first),
            new StaleProcess(2, ForeignServer),
            new StaleProcess(3, ExecutablePath: null),
            new StaleProcess(4, second)
        ]);
        var reaper = CreateReaper(scanner, BinariesRoot);

        await reaper.StartAsync(CancellationToken.None);

        AssertEx.Equal(expected: 2, scanner.KilledPids.Count);
        AssertEx.True(scanner.KilledPids.Contains(1), "Expected the first under-root orphan (pid 1) to be reaped.");
        AssertEx.True(scanner.KilledPids.Contains(4), "Expected the second under-root orphan (pid 4) to be reaped.");
    }

    [Test]
    public async Task StartAsync_WhenScannerThrows_DoesNotThrowAndDoesNotBlockStartup()
    {
        var scanner = new FakeStaleProcessScanner(candidates: [], throwOnEnumerate: true);
        var reaper = CreateReaper(scanner, BinariesRoot);

        // A scanner failure must be swallowed so it can never block application start.
        var start = reaper.StartAsync(CancellationToken.None);
        await start;

        AssertEx.True(start.IsCompletedSuccessfully, "A failing process scan must be swallowed so host startup continues.");
        AssertEx.Equal(expected: 1, scanner.EnumerateCallCount,
            "The scan must actually have been attempted, or this proves nothing about swallowing its failure.");
        AssertEx.Equal(expected: 0, scanner.KilledPids.Count);
    }

    [Test]
    public async Task StopAsync_Completes()
    {
        var scanner = new FakeStaleProcessScanner([]);
        var reaper = CreateReaper(scanner, BinariesRoot);

        await reaper.StopAsync(CancellationToken.None);

        AssertEx.Equal(expected: 0, scanner.KilledPids.Count);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task StartAsync_NamesTheExecutablePathOnlyWhenAsked(bool logExecutablePath)
    {
        // whisper-server's reaper has always left the path out of its per-orphan line; the other two include it.
        var ourServer = OurServerPath("b9700", "vulkan");
        var scanner = new FakeStaleProcessScanner([new StaleProcess(1234, ourServer)]);
        var logger = new RecordingLogger<StaleProcessReaper>();
        var reaper = new StaleProcessReaper(scanner, BinariesRoot, ServerName, logExecutablePath, logger);

        await reaper.StartAsync(CancellationToken.None);

        var reaping = logger.Entries.Single(entry => entry.Message.StartsWith("Reaping stale llama-server orphan (pid 1234)", StringComparison.Ordinal));
        AssertEx.Equal(logExecutablePath, reaping.Message.Contains(ourServer, StringComparison.Ordinal));
        AssertEx.Equal(expected: 1, scanner.KilledPids.Count);
    }

    private static StaleProcessReaper CreateReaper(IStaleProcessScanner scanner, string? binariesRoot) =>
        new(scanner, binariesRoot, ServerName, logExecutablePath: true, NullLogger<StaleProcessReaper>.Instance);

    private static string OurServerPath(string tag, string variant)
    {
        var serverName = OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server";
        return Path.Combine(BinariesRoot, tag, variant, $"llama-{tag}", serverName);
    }

    /// <summary>In-memory <see cref="IStaleProcessScanner" />: records kill calls and never touches a real process.</summary>
    private sealed class FakeStaleProcessScanner : IStaleProcessScanner
    {
        private readonly IReadOnlyList<StaleProcess> _candidates;
        private readonly bool _throwOnEnumerate;

        public FakeStaleProcessScanner(IReadOnlyList<StaleProcess> candidates, bool throwOnEnumerate = false)
        {
            _candidates = candidates;
            _throwOnEnumerate = throwOnEnumerate;
        }

        public List<int> KilledPids { get; } = [];

        public int EnumerateCallCount { get; private set; }

        public IReadOnlyList<StaleProcess> EnumerateProcesses()
        {
            EnumerateCallCount++;
            if (_throwOnEnumerate)
            {
                throw new InvalidOperationException("Simulated process-table read failure.");
            }

            return _candidates;
        }

        public void KillProcessTree(int pid)
        {
            KilledPids.Add(pid);
        }
    }
}
