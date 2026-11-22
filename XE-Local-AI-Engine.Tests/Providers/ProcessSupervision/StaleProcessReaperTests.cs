namespace XE_Local_AI_Engine.Tests.Providers.ProcessSupervision;

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.ProcessSupervision;
using XE_Local_AI_Engine.Providers.ProcessSupervision.Contracts;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

/// <summary>
///     Tests the startup <see cref="StaleProcessReaper" /> shared by llama-server, sd-server and whisper-server: it
///     reaps a previous-run orphan under the provider's own cache root, spares an unrelated same-name server and never
///     throws from <c>StartAsync</c>.
/// </summary>
/// <remarks>Matching runs through an in-memory scanner fake: no real processes, no file I/O.</remarks>
[Category(TestCategories.Unit)]
public sealed class StaleProcessReaperTests
{
    private const string ServerName = "llama-server";

    private static readonly string BinariesRoot = Path.Combine(Path.GetTempPath(), "xe-reaper-test", "llama.cpp");

    private static readonly string ForeignServer = OperatingSystem.IsWindows()
        ? @"C:\Program Files\OtherRuntime\llama-server.exe"
        : "/usr/lib/other-runtime/llama-server";

    [Test]
    public async Task StartAsync_WhenOrphanUnderBinariesRoot_TreeKillsIt()
    {
        var ourServer = OurServerPath("b9700", "vulkan");
        var scanner = new FakeStaleProcessScanner([new StaleProcess(1234, ourServer)]);
        var reaper = CreateReaper(scanner, BinariesRoot);

        await reaper.StartAsync(CancellationToken.None);

        AssertEx.Equal("1234", string.Join(',', scanner.SignalledPids));
    }

    [Test]
    public async Task StartAsync_WhenServerOutsideRoot_DoesNotTreeKillIt()
    {
        // An unrelated llama-server (other runtimes bundle one) must never be reaped.
        var scanner = new FakeStaleProcessScanner([new StaleProcess(4321, ForeignServer)]);
        var reaper = CreateReaper(scanner, BinariesRoot);

        await reaper.StartAsync(CancellationToken.None);

        AssertEx.Empty(scanner.SignalledPids);
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
        AssertEx.Equal("4242", string.Join(',', scanner.SignalledPids));
    }

    [Test]
    public async Task StartAsync_WhenSiblingPrefixPath_DoesNotTreeKillIt()
    {
        // ".../llama.cpp-other/..." shares a string prefix with the root ".../llama.cpp" but is NOT under it.
        var siblingPrefix = Path.Combine(Path.GetTempPath(), "xe-reaper-test", "llama.cpp-other", "llama-server");
        var scanner = new FakeStaleProcessScanner([new StaleProcess(7, siblingPrefix)]);
        var reaper = CreateReaper(scanner, BinariesRoot);

        await reaper.StartAsync(CancellationToken.None);

        AssertEx.Empty(scanner.SignalledPids);
    }

    [Test]
    public async Task StartAsync_WhenExecutablePathUnresolved_SkipsCandidate()
    {
        // A process whose executable path could not be read cannot be proven to be ours, so it is left alone.
        var scanner = new FakeStaleProcessScanner([new StaleProcess(99, ExecutablePath: null)]);
        var reaper = CreateReaper(scanner, BinariesRoot);

        await reaper.StartAsync(CancellationToken.None);

        AssertEx.Empty(scanner.SignalledPids);
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
        AssertEx.Empty(scanner.SignalledPids);
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

        AssertEx.Equal("1,4", string.Join(',', scanner.SignalledPids), "Only the two under-root orphans are reaped.");
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
        AssertEx.Empty(scanner.SignalledPids);
    }

    [Test]
    public async Task StopAsync_Completes()
    {
        var scanner = new FakeStaleProcessScanner([]);
        var reaper = CreateReaper(scanner, BinariesRoot);

        await reaper.StopAsync(CancellationToken.None);

        AssertEx.Empty(scanner.SignalledPids);
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
        AssertEx.Equal("1234", string.Join(',', scanner.SignalledPids));
    }

    // --- Spawn-receipt reap (Linux: the receipt store is disabled elsewhere) ---------------------------------------------------------

    [Test]
    [RunOn(OS.Linux)]
    public async Task Receipt_WhenTheScannerKillsTheRecordedProcess_PassesTheRecordedIdentity_AndRemovesIt()
    {
        // A bring-your-own binary lives OUTSIDE the managed root, so only the receipt can claim it.
        using var receipts = new ReceiptDirectory();
        receipts.Write(Receipt(5001, startTicks: 777, ForeignServer));
        var scanner = new FakeStaleProcessScanner([new StaleProcess(5001, ForeignServer)]);
        scanner.Stats[5001] = new ProcessStat(ParentProcessId: 1, ProcessGroupId: 5001, StartTicks: 777);

        await CreateReaper(scanner, BinariesRoot, receipts.Store).StartAsync(CancellationToken.None);

        AssertEx.Equal(new GuardedKill
        {
            Pid = 5001,
            ExecutablePath = ForeignServer,
            StartTicks = 777
        }, scanner.GuardedKills.Single(), "The scanner verifies the receipt's own identity after it pins the pid.");
        AssertEx.Equal(expected: 0, scanner.KilledPids.Count, "A receipt-reaped pid must not be killed a second time by the path match.");
        AssertEx.Equal(expected: 0, receipts.Count);
    }

    /// <summary>A recycled pid, another binary or a process gone before or after the pidfd open: the receipt can never match again.</summary>
    [Test]
    [RunOn(OS.Linux)]
    public async Task Receipt_WhenTheScannerReportsAMismatch_CountsNothing_AndRemovesIt()
    {
        using var receipts = new ReceiptDirectory();
        receipts.Write(Receipt(5008, startTicks: 777, ForeignServer));
        var scanner = new FakeStaleProcessScanner([new StaleProcess(5008, ForeignServer)])
        {
            GuardedKillResult = StaleProcessKillResult.Mismatch
        };
        var logger = new RecordingLogger<StaleProcessReaper>();

        await new StaleProcessReaper(scanner, BinariesRoot, ServerName, logExecutablePath: true, logger, receipts.Store).StartAsync(CancellationToken.None);

        AssertEx.Equal(expected: 1, scanner.GuardedKills.Count);
        AssertEx.False(logger.Entries.Any(entry => entry.Message.StartsWith("Reaped ", StringComparison.Ordinal)), "A refused signal is not a reap.");
        AssertEx.Equal(expected: 0, scanner.KilledPids.Count);
        AssertEx.Equal(expected: 0, receipts.Count);
    }

    /// <summary>An unreadable identity or a failed pidfd call learns nothing: the receipt stays for the next startup, and the path match must not tree-kill the pid.</summary>
    [Test]
    [RunOn(OS.Linux)]
    [Arguments(StaleProcessKillResult.Unreadable)]
    [Arguments(StaleProcessKillResult.Deferred)]
    public async Task Receipt_WhenTheIdentityCannotBeVerified_KeepsIt_AndNeverTreeKillsThePid(StaleProcessKillResult result)
    {
        var ourServer = OurServerPath("b9700", "cuda");
        using var receipts = new ReceiptDirectory();
        receipts.Write(Receipt(5005, startTicks: 777, ourServer));
        var scanner = new FakeStaleProcessScanner([new StaleProcess(5005, ourServer), new StaleProcess(5006, ourServer)]);
        scanner.GuardedKillResults[5005] = result;
        var logger = new RecordingLogger<StaleProcessReaper>();

        await new StaleProcessReaper(scanner, BinariesRoot, ServerName, logExecutablePath: true, logger, receipts.Store).StartAsync(CancellationToken.None);

        AssertEx.Equal("5005,5006", string.Join(',', scanner.SignalledPids), "The receipt's pid is tried once; only the unreceipted orphan is path-reaped.");
        AssertEx.Equal(expected: 1, receipts.Count, "A receipt that proved nothing is the only trace of a possibly live server.");
        AssertEx.True(logger.Entries.Any(entry => entry.Message.StartsWith("Reaped 1 ", StringComparison.Ordinal)), "The deferred pid is left out of the reaped count.");
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

    /// <summary>This run's own child, seen by the preliminary read or, when that failed, by the scanner after its open: its receipt stays, and the path match spares it.</summary>
    [Test]
    [RunOn(OS.Linux)]
    [Arguments("preliminary read", 0)]
    [Arguments("after the open", 1)]
    public async Task Receipt_WhenProcessIsThisHostsOwnChild_SignalsNothing_AndKeepsIt(string seen, int guardedKills)
    {
        var ourServer = OurServerPath("b9700", "cuda");
        using var receipts = new ReceiptDirectory();
        receipts.Write(Receipt(5007, startTicks: 777, ourServer));
        var scanner = new FakeStaleProcessScanner([new StaleProcess(5007, ourServer)])
        {
            GuardedKillResult = StaleProcessKillResult.OwnChild
        };
        if (seen == "preliminary read")
        {
            scanner.Stats[5007] = new ProcessStat(Environment.ProcessId, ProcessGroupId: 5007, StartTicks: 777);
        }

        await CreateReaper(scanner, BinariesRoot, receipts.Store).StartAsync(CancellationToken.None);

        AssertEx.Equal(guardedKills, scanner.GuardedKills.Count);
        AssertEx.Empty(scanner.KilledPids, "This host's own server sits under the managed root, yet the path match spares it.");
        AssertEx.Equal(expected: 1, receipts.Count);
    }

    /// <summary>A managed-root server whose receipt write failed is still this host's child, and an unreadable stat proves no owner: only the orphan is reaped.</summary>
    [Test]
    [RunOn(OS.Linux)]
    [Arguments("own child")]
    [Arguments("unreadable stat")]
    public async Task StartAsync_UnderTheRoot_NeverTreeKillsAProcessThatIsNotProvablyAnOrphan(string notOrphan)
    {
        var ourServer = OurServerPath("b9700", "cuda");
        using var receipts = new ReceiptDirectory();
        var scanner = new FakeStaleProcessScanner([new StaleProcess(5011, ourServer), new StaleProcess(5012, ourServer)]);
        if (notOrphan == "own child")
        {
            scanner.Stats[5011] = new ProcessStat(Environment.ProcessId, ProcessGroupId: 5011, StartTicks: 777);
        }
        else
        {
            _ = scanner.UnreadableStats.Add(5011);
        }

        await CreateReaper(scanner, BinariesRoot, receipts.Store).StartAsync(CancellationToken.None);

        AssertEx.Equal("5012", string.Join(',', scanner.SignalledPids));
    }

    /// <summary>A read failure (EMFILE, EACCES) says nothing about the file: it stays, and the pid its name records is spared by the path match.</summary>
    [Test]
    [RunOn(OS.Linux)]
    public async Task Receipt_WhenTheFileCannotBeRead_KeepsIt_AndNeverTreeKillsItsPid()
    {
        var ourServer = OurServerPath("b9700", "cuda");
        using var receipts = new ReceiptDirectory();
        receipts.WriteUnreadable(5005);
        var scanner = new FakeStaleProcessScanner([new StaleProcess(5005, ourServer), new StaleProcess(5006, ourServer)]);

        await CreateReaper(scanner, BinariesRoot, receipts.Store).StartAsync(CancellationToken.None);

        AssertEx.Equal("5006", string.Join(',', scanner.SignalledPids), "Only the unreceipted orphan under the root is path-reaped.");
        AssertEx.Equal(expected: 1, receipts.Count, "A receipt that could not be read may describe a live server.");
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

        await CreateReaper(scanner, BinariesRoot, receipts.Store).StartAsync(CancellationToken.None);

        AssertEx.Equal(new GuardedKill
        {
            Pid = 5010,
            ExecutablePath = byoServer,
            StartTicks = 777
        }, scanner.GuardedKills.Single());
        AssertEx.Equal(expected: 0, scanner.KilledPids.Count);
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

        AssertEx.Equal("6002", string.Join(',', scanner.SignalledPids));
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Receipts_StillReapWhenBinariesRootUnresolved()
    {
        using var receipts = new ReceiptDirectory();
        receipts.Write(Receipt(5009, startTicks: 9, ForeignServer));
        var scanner = new FakeStaleProcessScanner([new StaleProcess(5009, ForeignServer)]);

        await CreateReaper(scanner, binariesRoot: null, receipts.Store).StartAsync(CancellationToken.None);

        AssertEx.Equal(new GuardedKill
        {
            Pid = 5009,
            ExecutablePath = ForeignServer,
            StartTicks = 9
        }, scanner.GuardedKills.Single());
        AssertEx.Equal(expected: 0, scanner.KilledPids.Count);
    }

    // --- Boot binding: a receipt of another boot names no live process ---------------------------------------------------------------

    [Test]
    [RunOn(OS.Linux)]
    public async Task Receipt_FromAPreviousBoot_SignalsNothing_AndRemovesIt()
    {
        using var receipts = new ReceiptDirectory();
        receipts.Write(Receipt(5020, startTicks: 777, ForeignServer, bootId: "boot-before"));
        var scanner = new FakeStaleProcessScanner([new StaleProcess(5020, ForeignServer)]);

        await CreateReaper(scanner, BinariesRoot, receipts.Store).StartAsync(CancellationToken.None);

        AssertNothingSignalled(scanner);
        AssertEx.Equal(expected: 0, receipts.Count, "A receipt of a previous boot can never match again.");
    }

    /// <summary>The managed-root pass judges a previous boot's pid on its own: the pid now names a process of this boot that the receipt says nothing about.</summary>
    [Test]
    [RunOn(OS.Linux)]
    public async Task Receipt_FromAPreviousBoot_DoesNotClaimItsPid_FromTheManagedRootPass()
    {
        var ourServer = OurServerPath("b9700", "cuda");
        using var receipts = new ReceiptDirectory();
        receipts.Write(Receipt(5023, startTicks: 777, ourServer, bootId: "boot-before"));
        var scanner = new FakeStaleProcessScanner([new StaleProcess(5023, ourServer)]);
        scanner.Stats[5023] = new ProcessStat(ParentProcessId: 1, ProcessGroupId: 5023, StartTicks: 31);

        await CreateReaper(scanner, BinariesRoot, receipts.Store).StartAsync(CancellationToken.None);

        AssertEx.Equal(new GuardedKill
        {
            Pid = 5023,
            ExecutablePath = ourServer,
            StartTicks = 31
        }, scanner.GuardedKills.Single(), "Only the path match signals it, with the identity it read now.");
        AssertEx.Equal(expected: 0, receipts.Count);
    }

    /// <summary>A receipt of this boot, or one that records no boot, keeps every check it had; an unreadable current boot binds nothing to an unbound receipt.</summary>
    [Test]
    [RunOn(OS.Linux)]
    [Arguments("boot-now", "boot-now")]
    [Arguments(null, "boot-now")]
    [Arguments(null, null)]
    public async Task Receipt_FromThisBootOrUnbound_IsVerifiedAndReapedAsBefore(string? recordedBoot, string? currentBoot)
    {
        using var receipts = new ReceiptDirectory();
        receipts.Write(Receipt(5021, startTicks: 777, ForeignServer, recordedBoot));
        var scanner = new FakeStaleProcessScanner([])
        {
            CurrentBootId = currentBoot
        };

        await CreateReaper(scanner, BinariesRoot, receipts.Store).StartAsync(CancellationToken.None);

        AssertEx.Equal(new GuardedKill
        {
            Pid = 5021,
            ExecutablePath = ForeignServer,
            StartTicks = 777
        }, scanner.GuardedKills.Single());
        AssertEx.Equal(expected: 0, receipts.Count);
    }

    /// <summary>A receipt bound to a boot while this boot's id cannot be read proves nothing either way: it stays, and the path match spares its pid.</summary>
    [Test]
    [RunOn(OS.Linux)]
    public async Task Receipt_BoundToABoot_WhenTheCurrentBootIdCannotBeRead_KeepsIt_AndNeverSignalsItsPid()
    {
        var ourServer = OurServerPath("b9700", "cuda");
        using var receipts = new ReceiptDirectory();
        receipts.Write(Receipt(5021, startTicks: 777, ourServer, bootId: "boot-before"));
        var scanner = new FakeStaleProcessScanner([new StaleProcess(5021, ourServer), new StaleProcess(5022, ourServer)])
        {
            CurrentBootId = null
        };
        var logger = new RecordingLogger<StaleProcessReaper>();

        await new StaleProcessReaper(scanner, BinariesRoot, ServerName, logExecutablePath: true, logger, receipts.Store).StartAsync(CancellationToken.None);

        AssertEx.Equal("5022", string.Join(',', scanner.SignalledPids), "Only the unreceipted orphan under the root is path-reaped.");
        AssertEx.Equal(expected: 1, receipts.Count, "The receipt is kept for a startup that can read the boot id.");
        AssertEx.True(logger.Entries.Any(entry => entry.Level == LogLevel.Warning && entry.Message.Contains("pid 5021", StringComparison.Ordinal)));
    }

    // --- Managed-root pass on Linux: the identity it read, verified after a pidfd pinned it ----------------------------------------------

    /// <summary>A leader and a non-leader alike: the scanner decides group or pid, and a non-leader's descendants are no longer tree-killed here.</summary>
    [Test]
    [RunOn(OS.Linux)]
    [Arguments(5030)]
    [Arguments(4000)]
    public async Task PathReap_OnLinux_KillsOnlyTheProcessWhoseStatItRead_AndNeverTreeKills(int processGroupId)
    {
        var ourServer = OurServerPath("b9700", "cuda");
        var scanner = new FakeStaleProcessScanner([new StaleProcess(5030, ourServer)]);
        scanner.Stats[5030] = new ProcessStat(ParentProcessId: 1, processGroupId, StartTicks: 4321);
        var logger = new RecordingLogger<StaleProcessReaper>();

        await new StaleProcessReaper(scanner, BinariesRoot, ServerName, logExecutablePath: true, logger).StartAsync(CancellationToken.None);

        AssertEx.Equal(new GuardedKill
        {
            Pid = 5030,
            ExecutablePath = ourServer,
            StartTicks = 4321
        }, scanner.GuardedKills.Single(), "The start time is the one the ownership check read.");
        AssertEx.Empty(scanner.KilledPids, "A foreign pid is never tree-killed by number on Linux.");
        AssertEx.True(logger.Entries.Any(entry => entry.Message.StartsWith("Reaped 1 ", StringComparison.Ordinal)));
    }

    [Test]
    [ExcludeOn(OS.Linux)]
    public async Task PathReap_OffLinux_KeepsTheTreeKill()
    {
        var scanner = new FakeStaleProcessScanner([new StaleProcess(1234, OurServerPath("b9700", "vulkan"))]);

        await CreateReaper(scanner, BinariesRoot).StartAsync(CancellationToken.None);

        AssertEx.Equal("1234", string.Join(',', scanner.KilledPids));
        AssertEx.Empty(scanner.GuardedKills);
    }

    /// <summary>A pid that changed hands, could not be read or could not be pinned was not reaped: nothing is counted, and the miss is a warning.</summary>
    [Test]
    [RunOn(OS.Linux)]
    [Arguments(StaleProcessKillResult.Mismatch)]
    [Arguments(StaleProcessKillResult.Unreadable)]
    [Arguments(StaleProcessKillResult.Deferred)]
    public async Task PathReap_OnLinux_WhenTheIdentityIsNotVerified_CountsNothing_AndWarns(StaleProcessKillResult result)
    {
        var ourServer = OurServerPath("b9700", "cuda");
        var scanner = new FakeStaleProcessScanner([new StaleProcess(5031, ourServer)])
        {
            GuardedKillResult = result
        };
        var logger = new RecordingLogger<StaleProcessReaper>();

        await new StaleProcessReaper(scanner, BinariesRoot, ServerName, logExecutablePath: true, logger).StartAsync(CancellationToken.None);

        AssertEx.Equal(expected: 1, scanner.GuardedKills.Count);
        AssertEx.Empty(scanner.KilledPids);
        AssertEx.False(logger.Entries.Any(entry => entry.Message.StartsWith("Reaped ", StringComparison.Ordinal)), "A refused signal is not a reap.");
        AssertEx.True(logger.Entries.Any(entry => entry.Level == LogLevel.Warning && entry.Message.Contains("pid 5031", StringComparison.Ordinal)
                                                  && entry.Message.Contains(result.ToString(), StringComparison.Ordinal)));
    }

    private static void AssertNothingSignalled(FakeStaleProcessScanner scanner)
    {
        AssertEx.Equal(expected: 0, scanner.GuardedKills.Count, "A receipt that names no orphan must never reach the guarded kill.");
        AssertEx.Equal(expected: 0, scanner.KilledPids.Count, "A process outside the managed root must never be path-reaped.");
    }

    private static ProcessSpawnReceipt Receipt(int pid, long startTicks, string executablePath, string? bootId = null) =>
        new()
        {
            Pid = pid,
            StartTicks = startTicks,
            ExecutablePath = executablePath,
            Label = "model/Chat port 18080",
            BootId = bootId
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

        /// <summary>Pids whose stat read fails; any other pid without an entry reads as an orphan init adopted.</summary>
        public HashSet<int> UnreadableStats { get; } = [];

        public List<GuardedKill> GuardedKills { get; } = [];

        /// <summary>Every pid signalled, either way: through the identity-verified kill (receipts, and the path match on Linux), then the tree kill.</summary>
        public List<int> SignalledPids => [.. GuardedKills.Select(static kill => kill.Pid), .. KilledPids];

        public StaleProcessKillResult GuardedKillResult { get; init; } = StaleProcessKillResult.Killed;

        /// <summary>Per-pid answers of the guarded kill; any other pid gets <see cref="GuardedKillResult" />.</summary>
        public Dictionary<int, StaleProcessKillResult> GuardedKillResults { get; } = [];

        public string? CurrentBootId { get; init; } = "boot-now";

        public ProcessStat? ReadStat(int pid)
        {
            if (UnreadableStats.Contains(pid))
            {
                return null;
            }

            return Stats.TryGetValue(pid, out var stat) ? stat : new ProcessStat(ParentProcessId: 1, pid, StartTicks: 1);
        }

        public StaleProcessKillResult KillIfSameProcess(int pid, string expectedExecutablePath, long expectedStartTicks)
        {
            GuardedKills.Add(new GuardedKill
            {
                Pid = pid,
                ExecutablePath = expectedExecutablePath,
                StartTicks = expectedStartTicks
            });
            return GuardedKillResults.GetValueOrDefault(pid, GuardedKillResult);
        }
    }

    /// <summary>One guarded kill the reaper asked for, with the identity the scanner verifies.</summary>
    private sealed record GuardedKill
    {
        public required int Pid { get; init; }

        public required string ExecutablePath { get; init; }

        public required long StartTicks { get; init; }
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

        /// <summary>A receipt file that names itself as its link target: listed, yet every open fails with ELOOP, whoever runs the test.</summary>
        public void WriteUnreadable(int pid)
        {
            var path = Path.Combine(ReceiptsPath, $"{pid}.json");
            _ = File.CreateSymbolicLink(path, path);
        }

        public void Dispose() =>
            Directory.Delete(_root, recursive: true);
    }
}
