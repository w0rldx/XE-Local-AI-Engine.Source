namespace XE_Local_AI_Engine.Tests.Providers.StableDiffusionCpp;

using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Unit tests for the startup <see cref="StaleImageServerReaper" />: it must reap a previous-run sd-server orphan
///     whose binary lives under the app's own stable-diffusion.cpp cache root, leave any unrelated <c>sd-server</c>
///     untouched, and never throw out of <c>StartAsync</c>. All matching logic is exercised through an in-memory scanner
///     fake — no real processes and no real file I/O (the path filter is pure string normalization). Mirrors
///     <c>StaleLlamaServerReaperTests</c>.
/// </summary>
public sealed class StaleImageServerReaperTests
{
    private static readonly string BinariesRoot = Path.Combine(Path.GetTempPath(), "xe-sd-reaper-test", "stable-diffusion.cpp");

    [Test]
    public async Task StartAsync_WhenOrphanUnderBinariesRoot_TreeKillsIt()
    {
        var ourServer = OurServerPath("master-742-1a13107", "vulkan");
        var scanner = new FakeStaleImageServerProcessScanner([new StaleImageServerProcess(1234, ourServer)]);
        var reaper = new StaleImageServerReaper(scanner, BinariesRoot, NullLogger<StaleImageServerReaper>.Instance);

        await reaper.StartAsync(CancellationToken.None);

        AssertEx.Equal(expected: 1, scanner.KilledPids.Count);
        AssertEx.Equal(expected: 1234, scanner.KilledPids[0]);
    }

    [Test]
    public async Task StartAsync_WhenImageServerOutsideRoot_DoesNotTreeKillIt()
    {
        // An unrelated sd-server must never be reaped.
        var foreign = OperatingSystem.IsWindows()
            ? @"C:\Program Files\SomeOtherApp\sd-server.exe"
            : "/opt/some-other-app/sd-server";
        var scanner = new FakeStaleImageServerProcessScanner([new StaleImageServerProcess(4321, foreign)]);
        var reaper = new StaleImageServerReaper(scanner, BinariesRoot, NullLogger<StaleImageServerReaper>.Instance);

        await reaper.StartAsync(CancellationToken.None);

        AssertEx.Equal(expected: 0, scanner.KilledPids.Count);
    }

    [Test]
    public async Task StartAsync_WhenSiblingPrefixPath_DoesNotTreeKillIt()
    {
        // ".../stable-diffusion.cpp-other/..." shares a string prefix with the root but is NOT under it.
        var siblingPrefix = Path.Combine(Path.GetTempPath(), "xe-sd-reaper-test", "stable-diffusion.cpp-other", "sd-server");
        var scanner = new FakeStaleImageServerProcessScanner([new StaleImageServerProcess(7, siblingPrefix)]);
        var reaper = new StaleImageServerReaper(scanner, BinariesRoot, NullLogger<StaleImageServerReaper>.Instance);

        await reaper.StartAsync(CancellationToken.None);

        AssertEx.Equal(expected: 0, scanner.KilledPids.Count);
    }

    [Test]
    public async Task StartAsync_WhenExecutablePathUnresolved_SkipsCandidate()
    {
        var scanner = new FakeStaleImageServerProcessScanner([new StaleImageServerProcess(99, ExecutablePath: null)]);
        var reaper = new StaleImageServerReaper(scanner, BinariesRoot, NullLogger<StaleImageServerReaper>.Instance);

        await reaper.StartAsync(CancellationToken.None);

        AssertEx.Equal(expected: 0, scanner.KilledPids.Count);
    }

    [Test]
    public async Task StartAsync_WhenBinariesRootUnresolved_ReapsNothing()
    {
        var ourServer = OurServerPath("master-742-1a13107", "cpu");
        var scanner = new FakeStaleImageServerProcessScanner([new StaleImageServerProcess(1234, ourServer)]);
        var reaper = new StaleImageServerReaper(scanner, binariesRoot: null, NullLogger<StaleImageServerReaper>.Instance);

        await reaper.StartAsync(CancellationToken.None);

        // A null root disables the reap entirely — even a clearly-ours binary is left alone.
        AssertEx.False(scanner.WasEnumerated, "Scanner must not be consulted when the binaries root is unresolved.");
        AssertEx.Equal(expected: 0, scanner.KilledPids.Count);
    }

    [Test]
    public async Task StartAsync_WhenMultipleCandidates_ReapsOnlyThoseUnderRoot()
    {
        var first = OurServerPath("master-742-1a13107", "vulkan");
        var second = OurServerPath("master-742-1a13107", "cuda");
        var foreign = OperatingSystem.IsWindows()
            ? @"C:\Program Files\SomeOtherApp\sd-server.exe"
            : "/opt/some-other-app/sd-server";

        var scanner = new FakeStaleImageServerProcessScanner([
            new StaleImageServerProcess(1, first),
            new StaleImageServerProcess(2, foreign),
            new StaleImageServerProcess(3, ExecutablePath: null),
            new StaleImageServerProcess(4, second)
        ]);
        var reaper = new StaleImageServerReaper(scanner, BinariesRoot, NullLogger<StaleImageServerReaper>.Instance);

        await reaper.StartAsync(CancellationToken.None);

        AssertEx.Equal(expected: 2, scanner.KilledPids.Count);
        AssertEx.True(scanner.KilledPids.Contains(1), "Expected the first under-root orphan (pid 1) to be reaped.");
        AssertEx.True(scanner.KilledPids.Contains(4), "Expected the second under-root orphan (pid 4) to be reaped.");
    }

    [Test]
    public async Task StartAsync_WhenScannerThrows_DoesNotThrowAndDoesNotBlockStartup()
    {
        var scanner = new FakeStaleImageServerProcessScanner(candidates: [], throwOnEnumerate: true);
        var reaper = new StaleImageServerReaper(scanner, BinariesRoot, NullLogger<StaleImageServerReaper>.Instance);

        // A scanner failure must be swallowed so it can never block application start; the call completing is the assert.
        await reaper.StartAsync(CancellationToken.None);

        AssertEx.Equal(expected: 0, scanner.KilledPids.Count);
    }

    [Test]
    public async Task StopAsync_Completes()
    {
        var scanner = new FakeStaleImageServerProcessScanner([]);
        var reaper = new StaleImageServerReaper(scanner, BinariesRoot, NullLogger<StaleImageServerReaper>.Instance);

        await reaper.StopAsync(CancellationToken.None);

        AssertEx.Equal(expected: 0, scanner.KilledPids.Count);
    }

    private static string OurServerPath(string tag, string backend)
    {
        var serverName = OperatingSystem.IsWindows() ? "sd-server.exe" : "sd-server";
        return Path.Combine(BinariesRoot, tag, backend, serverName);
    }

    /// <summary>In-memory <see cref="IStaleImageServerProcessScanner" />: records kill calls and never touches a real process.</summary>
    private sealed class FakeStaleImageServerProcessScanner : IStaleImageServerProcessScanner
    {
        private readonly IReadOnlyList<StaleImageServerProcess> _candidates;
        private readonly bool _throwOnEnumerate;

        public FakeStaleImageServerProcessScanner(IReadOnlyList<StaleImageServerProcess> candidates, bool throwOnEnumerate = false)
        {
            _candidates = candidates;
            _throwOnEnumerate = throwOnEnumerate;
        }

        public List<int> KilledPids { get; } = [];

        public bool WasEnumerated { get; private set; }

        public IReadOnlyList<StaleImageServerProcess> EnumerateImageServerProcesses()
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
