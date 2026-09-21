namespace XE_Local_AI_Engine.Tests.AgentHome;

using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The run-retention sweep against a real filesystem the test owns: which runs each limit takes, which runs it
///     must never take whatever any limit says, and what the one audit line it writes contains.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class AgentHomeRunRetentionServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _dataRoot = new("xe-run-retention");
    private int _counter;

    public void Dispose() =>
        _dataRoot.Dispose();

    [Test]
    public async Task Sweep_DeletesRunsPastTheAgeLimitAndKeepsTheRest()
    {
        var old = SeedRun(Now.AddDays(-40));
        var recent = SeedRun(Now.AddDays(-2));
        var newest = SeedRun(Now.AddHours(-1));

        await SweepAsync(new AgentHomeRunRetentionOptions { RetentionDays = 30, MaxRuns = 0, MaxTotalBytes = 0 });

        AssertEx.False(Directory.Exists(old), "a run older than the age limit must be deleted.");
        AssertEx.True(Directory.Exists(recent), "a run inside the age window must survive.");
        AssertEx.True(Directory.Exists(newest), "the newest run is never deleted.");
    }

    [Test]
    public async Task Sweep_TrimsToTheRunCapOldestFirst()
    {
        var oldest = SeedRun(Now.AddDays(-5));
        var middle = SeedRun(Now.AddDays(-4));
        var newest = SeedRun(Now.AddDays(-3));

        await SweepAsync(new AgentHomeRunRetentionOptions { RetentionDays = 0, MaxRuns = 2, MaxTotalBytes = 0 });

        AssertEx.False(Directory.Exists(oldest), "the run cap evicts the oldest run first.");
        AssertEx.True(Directory.Exists(middle), "the cap is reached exactly, never overshot.");
        AssertEx.True(Directory.Exists(newest));
    }

    [Test]
    public async Task Sweep_EvictsOldestFirstUntilTheByteCeilingFits()
    {
        var oldest = SeedRun(Now.AddDays(-5), payloadBytes: 4000);
        var middle = SeedRun(Now.AddDays(-4), payloadBytes: 4000);
        var newest = SeedRun(Now.AddDays(-3), payloadBytes: 4000);

        await SweepAsync(new AgentHomeRunRetentionOptions { RetentionDays = 0, MaxRuns = 0, MaxTotalBytes = 9000 });

        AssertEx.False(Directory.Exists(oldest), "the byte ceiling evicts the oldest run first.");
        AssertEx.True(Directory.Exists(middle), "eviction stops as soon as the total fits, rather than emptying the directory.");
        AssertEx.True(Directory.Exists(newest));
    }

    [Test]
    public async Task Sweep_WithEveryLimitActive_AttributesEachDeletionToTheLimitThatTookIt()
    {
        SeedRun(Now.AddDays(-40), payloadBytes: 100);
        SeedRun(Now.AddDays(-5), payloadBytes: 4000);
        SeedRun(Now.AddDays(-4), payloadBytes: 4000);
        SeedRun(Now.AddDays(-3), payloadBytes: 4000);

        var logger = new RecordingLogger<AgentHomeRunRetentionService>();
        await SweepAsync(new AgentHomeRunRetentionOptions { RetentionDays = 30, MaxRuns = 3, MaxTotalBytes = 9000 }, logger: logger);

        var line = AuditLine(logger);
        AssertEx.Contains(line, "removed 2 run(s)");
        AssertEx.Contains(line, "1 past the age limit");
        AssertEx.Contains(line, "0 over the run cap");
        AssertEx.Contains(line, "1 over the byte cap");
    }

    /// <summary>
    ///     The audit line names the runs it removed — those ids are node-minted — and never the host path they sat at.
    /// </summary>
    [Test]
    public async Task Sweep_AuditLine_NamesTheRunIdsAndNoHostPath()
    {
        var removed = SeedRun(Now.AddDays(-40), payloadBytes: 1234);
        SeedRun(Now.AddHours(-1));

        var logger = new RecordingLogger<AgentHomeRunRetentionService>();
        await SweepAsync(new AgentHomeRunRetentionOptions { RetentionDays = 30, MaxRuns = 0, MaxTotalBytes = 0 }, logger: logger);

        var line = AuditLine(logger);
        AssertEx.Contains(line, Path.GetFileName(removed));
        AssertEx.Contains(line, "1234 byte(s)");
        AssertEx.False(line.Contains(_dataRoot.Path, StringComparison.Ordinal),
            "a host path in the audit line would leak where the operator's data directory is.");
    }

    [Test]
    public async Task Sweep_WhileTheExecutionLeaseIsHeld_DeletesNothing()
    {
        var old = SeedRun(Now.AddDays(-40));
        SeedRun(Now.AddHours(-1));

        await SweepAsync(new AgentHomeRunRetentionOptions { RetentionDays = 30 }, leaseHeld: true);

        AssertEx.True(Directory.Exists(old),
            "a run may be in flight while the lease is held, so the sweep waits rather than racing it.");
    }

    [Test]
    public async Task Sweep_LeavesARunInsideTheGracePeriodAlone()
    {
        var ancient = SeedRun(Now.AddDays(-40));
        var justStarted = SeedRun(Now.AddMinutes(-5));
        SeedRun(Now);

        await SweepAsync(new AgentHomeRunRetentionOptions { RetentionDays = 0, MaxRuns = 1, MaxTotalBytes = 0 });

        AssertEx.False(Directory.Exists(ancient), "the run cap still bites, or the grace assertion below proves nothing.");
        AssertEx.True(Directory.Exists(justStarted),
            "a run younger than the grace period may still be mid-append; the cap does not reach it.");
    }

    [Test]
    public async Task Sweep_LeavesADirectoryWhoseNameTheNodeDidNotMintAlone()
    {
        var foreign = Path.Combine(RunsRoot(), "not-a-run");
        Directory.CreateDirectory(foreign);
        var minted = SeedRun(Now.AddDays(-40));
        SeedRun(Now.AddHours(-1));

        await SweepAsync(new AgentHomeRunRetentionOptions { RetentionDays = 30, MaxRuns = 1, MaxTotalBytes = 1 });

        AssertEx.False(Directory.Exists(minted), "the sweep must have run, or the assertion below is vacuous.");
        AssertEx.True(Directory.Exists(foreign),
            "a name the sweep cannot classify has no knowable age, so it is never deleted.");
    }

    /// <summary>
    ///     A run directory that IS a link, and a link planted INSIDE a run: neither may take the sweep out of the runs
    ///     root. The first is refused outright, the second is removed as a link with its target untouched.
    /// </summary>
    [Test]
    public async Task Sweep_NeverFollowsALinkOutOfTheRunsRoot()
    {
        SymlinkSupport.EnsureSupported();

        var outside = Path.Combine(_dataRoot.Path, "outside");
        Directory.CreateDirectory(outside);
        var treasure = Path.Combine(outside, "keep.txt");
        await File.WriteAllTextAsync(treasure, "must survive");

        var linkedRun = Path.Combine(RunsRoot(), RunId(Now.AddDays(-40), counter: 90));
        Directory.CreateSymbolicLink(linkedRun, outside);

        var withInnerLink = SeedRun(Now.AddDays(-41), counter: 91);
        Directory.CreateSymbolicLink(Path.Combine(withInnerLink, "escape"), outside);

        SeedRun(Now.AddHours(-1));

        await SweepAsync(new AgentHomeRunRetentionOptions { RetentionDays = 30 });

        AssertEx.True(File.Exists(treasure), "nothing the sweep does may reach outside the runs root through a link.");
        AssertEx.True(Directory.Exists(linkedRun), "a run directory that is itself a link is unclassifiable, so it is left alone.");
        AssertEx.False(Directory.Exists(withInnerLink), "a run holding a link is still deleted — the link goes, its target stays.");
    }

    /// <summary>
    ///     Containment is asserted against a root spelled WITH a trailing separator, the spelling a naive prefix
    ///     comparison gets wrong, so the gate is exercised rather than assumed.
    /// </summary>
    [Test]
    public async Task Sweep_WithARootSpelledWithATrailingSeparator_StillDeletesTheRunsUnderIt()
    {
        var old = SeedRun(Now.AddDays(-40));
        SeedRun(Now.AddHours(-1));

        await SweepAsync(new AgentHomeRunRetentionOptions { RetentionDays = 30 },
            rootPath: Path.Combine(_dataRoot.Path, "agent-home-state") + Path.DirectorySeparatorChar);

        AssertEx.False(Directory.Exists(old),
            "the containment gate must accept the runs root however it is spelled, or retention silently stops working.");
    }

    [Test]
    [Arguments(true, false, "with AgentHome off nothing writes runs, so nothing deletes them either.")]
    [Arguments(false, true, "a disabled sweep is a clean no-op.")]
    public async Task ExecuteAsync_WhenEitherSwitchIsOff_SweepsNothing(bool retentionEnabled, bool agentHomeEnabled, string why)
    {
        var old = SeedRun(Now.AddDays(-40));

        using var service = CreateService(new AgentHomeRunRetentionOptions { Enabled = retentionEnabled, RetentionDays = 30 },
            new AgentHomeOptions { Enabled = agentHomeEnabled, RootPath = Path.Combine(_dataRoot.Path, "agent-home-state") },
            leaseHeld: false,
            new RecordingLogger<AgentHomeRunRetentionService>(),
            new ManualTimeProvider(Now));

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        AssertEx.True(Directory.Exists(old), why);
    }

    /// <summary>
    ///     A sweep that throws must not take the hosted service with it: the periodic loop still has to run, or one
    ///     transient failure turns retention off until the node restarts.
    /// </summary>
    [Test]
    public async Task ExecuteAsync_WhenASweepThrows_KeepsTheServiceRunningAndSweepsOnTheNextTick()
    {
        var old = SeedRun(Now.AddDays(-40));
        SeedRun(Now.AddHours(-1));

        var clock = new ManualTimeProvider(Now);
        var logger = new RecordingLogger<AgentHomeRunRetentionService>();
        using var service = new AgentHomeRunRetentionService(
            Options.Create(new AgentHomeRunRetentionOptions { RetentionDays = 30, SweepInterval = TimeSpan.FromHours(1) }),
            Options.Create(new AgentHomeOptions { Enabled = true, RootPath = Path.Combine(_dataRoot.Path, "agent-home-state") }),
            new FakeNodeDataDirectory(_dataRoot.Path),
            new ThrowOnceIdentityProvider(),
            new StubLeaseManager(held: false),
            clock,
            logger);

        await service.StartAsync(CancellationToken.None);

        AssertEx.True(Directory.Exists(old), "the first sweep threw, so nothing can have been deleted yet.");
        await AssertEx.EventuallyAsync(() => logger.HasEntry(LogLevel.Warning, "retention sweep failed"),
            TestBudgets.Contended,
            "a failed sweep reports itself rather than disappearing.");
        await AssertEx.EventuallyAsync(() => clock.ArmedTimerCount > 0,
            TestBudgets.Contended,
            "the service must reach its periodic timer after the startup sweep failed.");

        clock.Advance(TimeSpan.FromHours(1));
        await AssertEx.EventuallyAsync(() => !Directory.Exists(old),
            TestBudgets.Contended,
            "the periodic loop must survive a failed sweep and delete on the next tick.");

        await service.StopAsync(CancellationToken.None);
    }

    private static string AuditLine(RecordingLogger<AgentHomeRunRetentionService> logger) =>
        AssertEx.NotNull(logger.Entries.FirstOrDefault(static entry => entry.Message.Contains("retention removed", StringComparison.Ordinal)),
                "a sweep that deleted something writes exactly one audit line.")
                .Message;

    private async Task SweepAsync(AgentHomeRunRetentionOptions options,
        bool leaseHeld = false,
        string? rootPath = null,
        RecordingLogger<AgentHomeRunRetentionService>? logger = null)
    {
        using var service = CreateService(options,
            new AgentHomeOptions { Enabled = true, RootPath = rootPath ?? Path.Combine(_dataRoot.Path, "agent-home-state") },
            leaseHeld,
            logger ?? new RecordingLogger<AgentHomeRunRetentionService>(),
            new ManualTimeProvider(Now));
        await service.SweepAsync(CancellationToken.None);
    }

    private AgentHomeRunRetentionService CreateService(AgentHomeRunRetentionOptions options,
        AgentHomeOptions agentHomeOptions,
        bool leaseHeld,
        ILogger<AgentHomeRunRetentionService> logger,
        TimeProvider timeProvider) =>
        new(Options.Create(options),
            Options.Create(agentHomeOptions),
            new FakeNodeDataDirectory(_dataRoot.Path),
            new StaticIdentityProvider(),
            new StubLeaseManager(leaseHeld),
            timeProvider,
            logger);

    private string RunsRoot()
    {
        var root = Path.Combine(_dataRoot.Path, "agent-home-state", "agent-home", "runs");
        Directory.CreateDirectory(root);
        return root;
    }

    private static string RunId(DateTimeOffset startedAt, int counter) =>
        string.Create(CultureInfo.InvariantCulture, $"run-{startedAt.ToUnixTimeMilliseconds()}-{counter}");

    private string SeedRun(DateTimeOffset startedAt, int payloadBytes = 16, int? counter = null)
    {
        var directory = Path.Combine(RunsRoot(), RunId(startedAt, counter ?? ++_counter));
        Directory.CreateDirectory(Path.Combine(directory, "logs"));
        File.WriteAllText(Path.Combine(directory, "logs", "events.jsonl"), new string('x', payloadBytes));
        return directory;
    }

    private sealed class StaticIdentityProvider : IAgentHomeIdentityProvider
    {
        public Task<AgentHomeOwnerIdentity> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentHomeOwnerIdentity { OwnerUserId = "owner", NodeId = "node" });
    }

    private sealed class ThrowOnceIdentityProvider : IAgentHomeIdentityProvider
    {
        private int _calls;

        public Task<AgentHomeOwnerIdentity> GetAsync(CancellationToken cancellationToken = default) =>
            Interlocked.Increment(ref _calls) == 1
                ? Task.FromException<AgentHomeOwnerIdentity>(new InvalidOperationException("identity is unavailable."))
                : Task.FromResult(new AgentHomeOwnerIdentity { OwnerUserId = "owner", NodeId = "node" });
    }

    /// <summary>
    ///     The lease manager, answering one fixed held/not-held. Acquisition throws on purpose: a sweep that took the
    ///     lease out from under a live run would be a correctness bug, so the double proves it never tries.
    /// </summary>
    private sealed class StubLeaseManager : IAgentHomeExecutionLeaseManager
    {
        private readonly bool _held;

        public StubLeaseManager(bool held)
        {
            _held = held;
        }

        public IAgentHomeExecutionLease? TryAcquire(AgentHomeExecutionLeaseKey key) =>
            throw new InvalidOperationException("The retention sweep must never acquire the execution lease.");

        public IAgentHomeExecutionLease? TryAcquireForRecovery(AgentHomeExecutionLeaseKey key) =>
            throw new InvalidOperationException("The retention sweep must never acquire the execution lease.");

        public bool IsHeld(AgentHomeExecutionLeaseKey key) =>
            _held;

        public bool IsPoisoned(AgentHomeExecutionLeaseKey key) =>
            false;

        public void MarkPoisoned(AgentHomeExecutionLeaseKey key)
        {
        }

        public void ClearPoison(AgentHomeExecutionLeaseKey key)
        {
        }
    }
}
