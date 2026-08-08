namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Unit tests for the startup <see cref="StaleLlamaServerReaper" />: it must reap a previous-run llama-server orphan
///     whose binary lives under the app's own llama.cpp cache root, leave any unrelated <c>llama-server</c> (e.g. Ollama's)
///     untouched, and never throw out of <c>StartAsync</c>. All matching logic is exercised through an in-memory scanner
///     fake — no real processes and no real file I/O (the path filter is pure string normalization).
/// </summary>
public sealed class StaleLlamaServerReaperTests
{
    private static readonly string BinariesRoot = Path.Combine(Path.GetTempPath(), "xe-reaper-test", "llama.cpp");

    [Test]
    public async Task StartAsync_WhenOrphanUnderBinariesRoot_TreeKillsIt()
    {
        var ourServer = OurServerPath("b9700", "vulkan");
        var scanner = new FakeStaleLlamaServerProcessScanner([new StaleLlamaServerProcess(1234, ourServer)]);
        var reaper = new StaleLlamaServerReaper(scanner, BinariesRoot, NullLogger<StaleLlamaServerReaper>.Instance);

        await reaper.StartAsync(CancellationToken.None);

        AssertEx.Equal(expected: 1, scanner.KilledPids.Count);
        AssertEx.Equal(expected: 1234, scanner.KilledPids[0]);
    }

    [Test]
    public async Task StartAsync_WhenLlamaServerOutsideRoot_DoesNotTreeKillIt()
    {
        // An unrelated llama-server (Ollama bundles one) must never be reaped.
        var foreign = OperatingSystem.IsWindows()
            ? @"C:\Program Files\Ollama\llama-server.exe"
            : "/usr/lib/ollama/llama-server";
        var scanner = new FakeStaleLlamaServerProcessScanner([new StaleLlamaServerProcess(4321, foreign)]);
        var reaper = new StaleLlamaServerReaper(scanner, BinariesRoot, NullLogger<StaleLlamaServerReaper>.Instance);

        await reaper.StartAsync(CancellationToken.None);

        AssertEx.Equal(expected: 0, scanner.KilledPids.Count);
    }

    [Test]
    public async Task StartAsync_WhenSiblingPrefixPath_DoesNotTreeKillIt()
    {
        // ".../llama.cpp-other/..." shares a string prefix with the root ".../llama.cpp" but is NOT under it.
        var siblingPrefix = Path.Combine(Path.GetTempPath(), "xe-reaper-test", "llama.cpp-other", "llama-server");
        var scanner = new FakeStaleLlamaServerProcessScanner([new StaleLlamaServerProcess(7, siblingPrefix)]);
        var reaper = new StaleLlamaServerReaper(scanner, BinariesRoot, NullLogger<StaleLlamaServerReaper>.Instance);

        await reaper.StartAsync(CancellationToken.None);

        AssertEx.Equal(expected: 0, scanner.KilledPids.Count);
    }

    [Test]
    public async Task StartAsync_WhenExecutablePathUnresolved_SkipsCandidate()
    {
        var scanner = new FakeStaleLlamaServerProcessScanner([new StaleLlamaServerProcess(99, ExecutablePath: null)]);
        var reaper = new StaleLlamaServerReaper(scanner, BinariesRoot, NullLogger<StaleLlamaServerReaper>.Instance);

        await reaper.StartAsync(CancellationToken.None);

        AssertEx.Equal(expected: 0, scanner.KilledPids.Count);
    }

    [Test]
    public async Task StartAsync_WhenBinariesRootUnresolved_ReapsNothing()
    {
        var ourServer = OurServerPath("b9700", "cpu");
        var scanner = new FakeStaleLlamaServerProcessScanner([new StaleLlamaServerProcess(1234, ourServer)]);
        var reaper = new StaleLlamaServerReaper(scanner, binariesRoot: null, NullLogger<StaleLlamaServerReaper>.Instance);

        await reaper.StartAsync(CancellationToken.None);

        // A null root disables the reap entirely — even a clearly-ours binary is left alone.
        AssertEx.False(scanner.WasEnumerated, "Scanner must not be consulted when the binaries root is unresolved.");
        AssertEx.Equal(expected: 0, scanner.KilledPids.Count);
    }

    [Test]
    public async Task StartAsync_WhenMultipleCandidates_ReapsOnlyThoseUnderRoot()
    {
        var first = OurServerPath("b9700", "vulkan");
        var second = OurServerPath("b9692", "cuda");
        var foreign = OperatingSystem.IsWindows()
            ? @"C:\Program Files\Ollama\llama-server.exe"
            : "/usr/lib/ollama/llama-server";

        var scanner = new FakeStaleLlamaServerProcessScanner([
            new StaleLlamaServerProcess(1, first),
            new StaleLlamaServerProcess(2, foreign),
            new StaleLlamaServerProcess(3, ExecutablePath: null),
            new StaleLlamaServerProcess(4, second)
        ]);
        var reaper = new StaleLlamaServerReaper(scanner, BinariesRoot, NullLogger<StaleLlamaServerReaper>.Instance);

        await reaper.StartAsync(CancellationToken.None);

        AssertEx.Equal(expected: 2, scanner.KilledPids.Count);
        AssertEx.True(scanner.KilledPids.Contains(1), "Expected the first under-root orphan (pid 1) to be reaped.");
        AssertEx.True(scanner.KilledPids.Contains(4), "Expected the second under-root orphan (pid 4) to be reaped.");
    }

    [Test]
    public async Task StartAsync_WhenScannerThrows_DoesNotThrowAndDoesNotBlockStartup()
    {
        var scanner = new FakeStaleLlamaServerProcessScanner(candidates: [], throwOnEnumerate: true);
        var reaper = new StaleLlamaServerReaper(scanner, BinariesRoot, NullLogger<StaleLlamaServerReaper>.Instance);

        // A scanner failure must be swallowed so it can never block application start; the call completing is the assert.
        await reaper.StartAsync(CancellationToken.None);

        AssertEx.Equal(expected: 0, scanner.KilledPids.Count);
    }

    [Test]
    public async Task StopAsync_Completes()
    {
        var scanner = new FakeStaleLlamaServerProcessScanner([]);
        var reaper = new StaleLlamaServerReaper(scanner, BinariesRoot, NullLogger<StaleLlamaServerReaper>.Instance);

        await reaper.StopAsync(CancellationToken.None);

        AssertEx.Equal(expected: 0, scanner.KilledPids.Count);
    }

    private static string OurServerPath(string tag, string variant)
    {
        var serverName = OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server";
        return Path.Combine(BinariesRoot, tag, variant, $"llama-{tag}", serverName);
    }

    /// <summary>In-memory <see cref="IStaleLlamaServerProcessScanner" />: records kill calls and never touches a real process.</summary>
    private sealed class FakeStaleLlamaServerProcessScanner : IStaleLlamaServerProcessScanner
    {
        private readonly IReadOnlyList<StaleLlamaServerProcess> _candidates;
        private readonly bool _throwOnEnumerate;

        public FakeStaleLlamaServerProcessScanner(IReadOnlyList<StaleLlamaServerProcess> candidates, bool throwOnEnumerate = false)
        {
            _candidates = candidates;
            _throwOnEnumerate = throwOnEnumerate;
        }

        public List<int> KilledPids { get; } = [];

        public bool WasEnumerated { get; private set; }

        public IReadOnlyList<StaleLlamaServerProcess> EnumerateLlamaServerProcesses()
        {
            WasEnumerated = true;
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
