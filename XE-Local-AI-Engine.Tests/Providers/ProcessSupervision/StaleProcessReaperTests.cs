namespace XE_Local_AI_Engine.Tests.Providers.ProcessSupervision;

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.ProcessSupervision;
using XE_Local_AI_Engine.Providers.ProcessSupervision.Contracts;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

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

    // --- Spawn-receipt reap (Linux: the receipt store is disabled elsewhere) ---------------------------------------------------------

    [Test]
    [RunOn(OS.Linux)]
    public async Task Receipt_WhenPidStartTimeAndExecutableAllMatch_KillsWithRecordedStartTime_AndRemovesIt()
    {
        // A bring-your-own binary lives OUTSIDE the managed root, so only the receipt can claim it.
        using var receipts = new ReceiptDirectory();
        receipts.Write(Receipt(5001, startTicks: 777, ForeignServer));
        var scanner = new FakeStaleProcessScanner([new StaleProcess(5001, ForeignServer)]);
        scanner.Stats[5001] = new ProcessStat(ParentProcessId: 1, ProcessGroupId: 5001, StartTicks: 777);

        await CreateReaper(scanner, BinariesRoot, receipts.Store).StartAsync(CancellationToken.None);

        AssertEx.Equal(expected: 1, scanner.GuardedKills.Count);
        AssertEx.Equal((5001, 777L), scanner.GuardedKills[0]);
        AssertEx.Equal(expected: 0, scanner.KilledPids.Count, "A receipt-reaped pid must not be killed a second time by the path match.");
        AssertEx.Equal(expected: 0, receipts.Count);
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Receipt_WhenStartTimeDiffers_PidReuse_SignalsNothing_AndRemovesIt()
    {
        using var receipts = new ReceiptDirectory();
        receipts.Write(Receipt(5002, startTicks: 777, ForeignServer));
        var scanner = new FakeStaleProcessScanner([new StaleProcess(5002, ForeignServer)]);
        scanner.Stats[5002] = new ProcessStat(ParentProcessId: 1, ProcessGroupId: 5002, StartTicks: 778);

        await CreateReaper(scanner, BinariesRoot, receipts.Store).StartAsync(CancellationToken.None);

        AssertNothingSignalled(scanner);
        AssertEx.Equal(expected: 0, receipts.Count);
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Receipt_WhenExecutableDiffers_SignalsNothing_AndRemovesIt()
    {
        using var receipts = new ReceiptDirectory();
        receipts.Write(Receipt(5003, startTicks: 777, "/opt/other-build/llama-server"));
        var scanner = new FakeStaleProcessScanner([new StaleProcess(5003, ForeignServer)]);
        scanner.Stats[5003] = new ProcessStat(ParentProcessId: 1, ProcessGroupId: 5003, StartTicks: 777);

        await CreateReaper(scanner, BinariesRoot, receipts.Store).StartAsync(CancellationToken.None);

        AssertNothingSignalled(scanner);
        AssertEx.Equal(expected: 0, receipts.Count);
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Receipt_WhenProcessGone_SignalsNothing_AndRemovesIt()
    {
        using var receipts = new ReceiptDirectory();
        receipts.Write(Receipt(5004, startTicks: 777, ForeignServer));
        var scanner = new FakeStaleProcessScanner([]);

        await CreateReaper(scanner, BinariesRoot, receipts.Store).StartAsync(CancellationToken.None);

        AssertNothingSignalled(scanner);
        AssertEx.Equal(expected: 0, receipts.Count);
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Receipt_WhenProcStatUnreadable_SignalsNothing_AndRemovesIt()
    {
        // The process is listed with the right binary, but its /proc stat cannot be read: identity is unproven.
        using var receipts = new ReceiptDirectory();
        receipts.Write(Receipt(5005, startTicks: 777, ForeignServer));
        var scanner = new FakeStaleProcessScanner([new StaleProcess(5005, ForeignServer)]);

        await CreateReaper(scanner, BinariesRoot, receipts.Store).StartAsync(CancellationToken.None);

        AssertNothingSignalled(scanner);
        AssertEx.Equal(expected: 0, receipts.Count);
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Receipt_WhenUnparseable_SignalsNothing_AndRemovesIt()
    {
        using var receipts = new ReceiptDirectory();
        receipts.WriteRaw(5006, "{ not json");
        var scanner = new FakeStaleProcessScanner([new StaleProcess(5006, ForeignServer)]);

        await CreateReaper(scanner, BinariesRoot, receipts.Store).StartAsync(CancellationToken.None);

        AssertNothingSignalled(scanner);
        AssertEx.Equal(expected: 0, receipts.Count);
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Receipt_WhenProcessIsThisHostsOwnChild_SignalsNothing_AndKeepsIt()
    {
        // Spawned by this run before the reaper got to it: live, ours, and its receipt is still current.
        using var receipts = new ReceiptDirectory();
        receipts.Write(Receipt(5007, startTicks: 777, ForeignServer));
        var scanner = new FakeStaleProcessScanner([new StaleProcess(5007, ForeignServer)]);
        scanner.Stats[5007] = new ProcessStat(Environment.ProcessId, ProcessGroupId: 5007, StartTicks: 777);

        await CreateReaper(scanner, BinariesRoot, receipts.Store).StartAsync(CancellationToken.None);

        AssertNothingSignalled(scanner);
        AssertEx.Equal(expected: 1, receipts.Count);
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Receipt_WhenIdentityChangesBeforeTheSignal_CountsNothing_AndRemovesIt()
    {
        using var receipts = new ReceiptDirectory();
        receipts.Write(Receipt(5008, startTicks: 777, ForeignServer));
        var scanner = new FakeStaleProcessScanner([new StaleProcess(5008, ForeignServer)])
        {
            GuardedKillResult = false
        };
        scanner.Stats[5008] = new ProcessStat(ParentProcessId: 1, ProcessGroupId: 5008, StartTicks: 777);
        var logger = new RecordingLogger<StaleProcessReaper>();

        await new StaleProcessReaper(scanner, BinariesRoot, ServerName, logExecutablePath: true, logger, receipts.Store).StartAsync(CancellationToken.None);

        AssertEx.Equal(expected: 1, scanner.GuardedKills.Count, "The guarded kill is the one that re-reads the start time.");
        AssertEx.False(logger.Entries.Any(entry => entry.Message.StartsWith("Reaped ", StringComparison.Ordinal)), "A refused signal is not a reap.");
        AssertEx.Equal(expected: 0, receipts.Count);
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Receipt_ForABinaryNamedUnlikeTheScannedProcess_IsReapedByPid()
    {
        // A bring-your-own binary may carry any file name, so the name-filtered scan never lists it; the receipt resolves the pid itself.
        const string byoServer = "/opt/byo/llama-server-cuda";
        using var receipts = new ReceiptDirectory();
        receipts.Write(Receipt(5010, startTicks: 777, byoServer));
        var scanner = new FakeStaleProcessScanner([]);
        scanner.Executables[5010] = byoServer;
        scanner.Stats[5010] = new ProcessStat(ParentProcessId: 1, ProcessGroupId: 5010, StartTicks: 777);

        await CreateReaper(scanner, BinariesRoot, receipts.Store).StartAsync(CancellationToken.None);

        AssertEx.Equal(expected: 1, scanner.GuardedKills.Count);
        AssertEx.Equal((5010, 777L), scanner.GuardedKills[0]);
        AssertEx.Equal(expected: 0, scanner.KilledPids.Count);
        AssertEx.Equal(expected: 0, receipts.Count);
    }

    [Test]
    [RunOn(OS.Linux)]
    [Arguments("/usr/bin/bash", 777L)]
    [Arguments("/opt/byo/llama-server-cuda", 778L)]
    public async Task Receipt_WhenPidIsNowAnUnrelatedProcess_SignalsNothing_AndRemovesIt(string liveExecutable, long liveStartTicks)
    {
        // The recorded pid was recycled: a different binary, or the same binary started later. Neither is the process this node spawned.
        using var receipts = new ReceiptDirectory();
        receipts.Write(Receipt(5011, startTicks: 777, "/opt/byo/llama-server-cuda"));
        var scanner = new FakeStaleProcessScanner([]);
        scanner.Executables[5011] = liveExecutable;
        scanner.Stats[5011] = new ProcessStat(ParentProcessId: 1, ProcessGroupId: 5011, liveStartTicks);

        await CreateReaper(scanner, BinariesRoot, receipts.Store).StartAsync(CancellationToken.None);

        AssertNothingSignalled(scanner);
        AssertEx.Equal(expected: 0, receipts.Count);
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Receipts_Enabled_SameBinaryWithoutReceipt_OutsideRoot_IsNotReaped_WhileManagedRootOrphanStillIs()
    {
        // Another host's process of the same BYO binary has no receipt here, so nothing claims it; the managed-root path match is unchanged.
        using var receipts = new ReceiptDirectory();
        var ourManaged = OurServerPath("b9700", "cuda");
        var scanner = new FakeStaleProcessScanner([new StaleProcess(6001, ForeignServer), new StaleProcess(6002, ourManaged)]);
        scanner.Stats[6001] = new ProcessStat(ParentProcessId: 1, ProcessGroupId: 6001, StartTicks: 1);

        await CreateReaper(scanner, BinariesRoot, receipts.Store).StartAsync(CancellationToken.None);

        AssertEx.Equal(expected: 0, scanner.GuardedKills.Count);
        AssertEx.Equal(expected: 1, scanner.KilledPids.Count);
        AssertEx.Equal(expected: 6002, scanner.KilledPids[0]);
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Receipts_StillReapWhenBinariesRootUnresolved()
    {
        using var receipts = new ReceiptDirectory();
        receipts.Write(Receipt(5009, startTicks: 9, ForeignServer));
        var scanner = new FakeStaleProcessScanner([new StaleProcess(5009, ForeignServer)]);
        scanner.Stats[5009] = new ProcessStat(ParentProcessId: 1, ProcessGroupId: 5009, StartTicks: 9);

        await CreateReaper(scanner, binariesRoot: null, receipts.Store).StartAsync(CancellationToken.None);

        AssertEx.Equal(expected: 1, scanner.GuardedKills.Count);
        AssertEx.Equal((5009, 9L), scanner.GuardedKills[0]);
        AssertEx.Equal(expected: 0, scanner.KilledPids.Count);
    }

    private static void AssertNothingSignalled(FakeStaleProcessScanner scanner)
    {
        AssertEx.Equal(expected: 0, scanner.GuardedKills.Count, "A partial identity match must never signal.");
        AssertEx.Equal(expected: 0, scanner.KilledPids.Count, "A process outside the managed root must never be path-reaped.");
    }

    private static ProcessSpawnReceipt Receipt(int pid, long startTicks, string executablePath) =>
        new()
        {
            Pid = pid,
            StartTicks = startTicks,
            ExecutablePath = executablePath,
            Label = "model/Chat port 18080"
        };

    private static StaleProcessReaper CreateReaper(IStaleProcessScanner scanner, string? binariesRoot, ProcessSpawnReceiptStore receipts) =>
        new(scanner, binariesRoot, ServerName, logExecutablePath: true, NullLogger<StaleProcessReaper>.Instance, receipts);

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

            // A process the name scan lists has a readable executable; tests add pids the scan never sees.
            foreach (var candidate in candidates)
            {
                if (candidate.ExecutablePath is { } path)
                {
                    Executables[candidate.Pid] = path;
                }
            }
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

        public Dictionary<int, ProcessStat> Stats { get; } = [];

        public Dictionary<int, string> Executables { get; } = [];

        public List<(int Pid, long StartTicks)> GuardedKills { get; } = [];

        public bool GuardedKillResult { get; init; } = true;

        public ProcessStat? ReadStat(int pid) =>
            Stats.TryGetValue(pid, out var stat) ? stat : null;

        public string? ReadExecutablePath(int pid) =>
            Executables.GetValueOrDefault(pid);

        public bool KillIfSameProcess(int pid, long expectedStartTicks)
        {
            GuardedKills.Add((pid, expectedStartTicks));
            return GuardedKillResult;
        }
    }

    /// <summary>A throwaway node data root whose <c>runtime/llama-server</c> directory the reaper's store reads.</summary>
    private sealed class ReceiptDirectory : IDisposable
    {
        private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

        private readonly string _root = Path.Combine(Path.GetTempPath(), $"xe-receipts-{Guid.NewGuid():N}");

        public ReceiptDirectory()
        {
            Store = new ProcessSpawnReceiptStore(_root, ServerName, NullLogger.Instance);
            Directory.CreateDirectory(ReceiptsPath);
        }

        public ProcessSpawnReceiptStore Store { get; }

        public int Count => Directory.EnumerateFiles(ReceiptsPath, "*.json").Count();

        private string ReceiptsPath => Path.Combine(_root, "runtime", ServerName);

        public void Write(ProcessSpawnReceipt receipt) =>
            WriteRaw(receipt.Pid, JsonSerializer.Serialize(receipt, WebJson));

        public void WriteRaw(int pid, string json) =>
            File.WriteAllText(Path.Combine(ReceiptsPath, $"{pid}.json"), json);

        public void Dispose() =>
            Directory.Delete(_root, recursive: true);
    }
}
