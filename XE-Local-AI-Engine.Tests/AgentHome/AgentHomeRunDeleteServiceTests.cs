namespace XE_Local_AI_Engine.Tests.AgentHome;

using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

/// <summary>
///     The operator-initiated run delete against a real filesystem the test owns: what it removes, what it refuses to
///     compose a path out of, and what it leaves standing while a run is in flight.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class AgentHomeRunDeleteServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _dataRoot = new("xe-run-delete");
    private int _counter;

    public void Dispose() =>
        _dataRoot.Dispose();

    [Test]
    public async Task DeleteAsync_RemovesTheRunDirectoryAndEverythingUnderIt()
    {
        var run = SeedRun(Now.AddDays(-1));

        var outcome = await Service().DeleteAsync(Path.GetFileName(run));

        AssertEx.Equal(AgentHomeRunDeleteOutcome.Deleted, outcome);
        AssertEx.False(Directory.Exists(run), "a deleted run leaves nothing behind — log, commands and patch all go.");
    }

    [Test]
    public async Task DeleteAsync_DeletesARunWhosePatchWasAlreadyApplied()
    {
        var run = SeedRun(Now.AddDays(-1));
        var patches = Path.Combine(run, "patches");
        Directory.CreateDirectory(patches);
        await File.WriteAllTextAsync(Path.Combine(patches, "changes.patch"), "diff --git a/x b/x\n");
        await File.AppendAllTextAsync(Path.Combine(run, "logs", "events.jsonl"), "{\"eventName\":\"patch_applied\"}\n");

        var outcome = await Service().DeleteAsync(Path.GetFileName(run));

        AssertEx.Equal(AgentHomeRunDeleteOutcome.Deleted, outcome,
            "an apply wrote into the operator's own folders, not into the run: deleting the run reverts nothing, so "
            + "there is nothing here to protect.");
    }

    [Test]
    public async Task DeleteAsync_WithAnUnknownRunId_AnswersNotFoundAndTouchesNothing()
    {
        var other = SeedRun(Now.AddDays(-1));

        var outcome = await Service().DeleteAsync("run-1758300000000-999");

        AssertEx.Equal(AgentHomeRunDeleteOutcome.NotFound, outcome);
        AssertEx.True(Directory.Exists(other), "an unknown id must not reach any run that does exist.");
    }

    [Test]
    [Arguments("..", "a bare parent reference")]
    [Arguments("../..", "a climb out of the runs root")]
    [Arguments("not-a-run", "a name the node never minted")]
    [Arguments("run-notanumber-1", "a run-shaped name whose timestamp is not one")]
    [Arguments("run-1758300000000-1/../../escape", "a traversal hidden behind a well-formed prefix")]
    [Arguments("%2e%2e%2f%2e%2e", "a percent-encoded climb, already decoded by the time a service sees it")]
    public async Task DeleteAsync_WithATraversalOrUnmintedId_AnswersNotFoundWithoutComposingAPath(string runId, string why)
    {
        var sentinel = Path.Combine(_dataRoot.Path, "sentinel");
        Directory.CreateDirectory(sentinel);
        SeedRun(Now.AddDays(-1));

        var outcome = await Service().DeleteAsync(runId);

        AssertEx.Equal(AgentHomeRunDeleteOutcome.NotFound, outcome, why);
        AssertEx.True(Directory.Exists(sentinel), "nothing outside the runs root may be reachable from a run id.");
        AssertEx.True(Directory.Exists(RunsRoot()), "the runs root itself must survive every malformed id.");
    }

    [Test]
    public async Task DeleteAsync_WithAnAbsolutePathAsTheRunId_AnswersNotFound()
    {
        var sentinel = Path.Combine(_dataRoot.Path, "sentinel");
        Directory.CreateDirectory(sentinel);

        var outcome = await Service().DeleteAsync(sentinel);

        AssertEx.Equal(AgentHomeRunDeleteOutcome.NotFound, outcome,
            "a rooted name would make Path.Combine discard the runs root entirely.");
        AssertEx.True(Directory.Exists(sentinel));
    }

    [Test]
    public async Task DeleteAsync_WithALinkedRunDirectory_RefusesRatherThanFollowingIt()
    {
        var target = Path.Combine(_dataRoot.Path, "outside-the-runs-root");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "payload.txt"), "not the node's to delete");

        SymlinkSupport.EnsureSupported();
        var runId = RunId(Now.AddDays(-1), ++_counter);
        Directory.CreateSymbolicLink(Path.Combine(RunsRoot(), runId), target);

        var outcome = await Service().DeleteAsync(runId);

        AssertEx.Equal(AgentHomeRunDeleteOutcome.NotFound, outcome, "a link is never treated as the tree it points at.");
        AssertEx.True(Directory.Exists(target), "the link's target must be untouched.");
        AssertEx.True(File.Exists(Path.Combine(target, "payload.txt")));
    }

    [Test]
    public async Task DeleteAsync_OfARunThatIsExecuting_AnswersConflictAndLeavesTheRunIntact()
    {
        var run = SeedRun(Now.AddDays(-1));
        var executing = new AgentHomeRunExecutionRegistry();

        using var scope = executing.Begin(Path.GetFileName(run));
        var outcome = await Service(executingRuns: executing).DeleteAsync(Path.GetFileName(run));

        AssertEx.Equal(AgentHomeRunDeleteOutcome.Conflict, outcome);
        AssertEx.True(Directory.Exists(run), "deleting under a live run destroys work irrecoverably; it must refuse.");
        AssertEx.True(File.Exists(Path.Combine(run, "logs", "events.jsonl")), "the run's log must still be there.");
    }

    /// <summary>
    ///     The bug the per-run signal fixes: the execution lease is keyed per owner-node SANDBOX, so reading it
    ///     refused every finished run's delete whenever anything at all was touching that sandbox.
    /// </summary>
    [Test]
    public async Task DeleteAsync_OfAFinishedRunWhileAnotherRunIsExecuting_StillDeletesIt()
    {
        var finished = SeedRun(Now.AddDays(-2));
        var live = SeedRun(Now.AddDays(-1));
        var executing = new AgentHomeRunExecutionRegistry();

        using var scope = executing.Begin(Path.GetFileName(live));
        var outcome = await Service(executingRuns: executing).DeleteAsync(Path.GetFileName(finished));

        AssertEx.Equal(AgentHomeRunDeleteOutcome.Deleted, outcome,
            "a run that finished hours ago is not in flight because something else is holding the sandbox.");
        AssertEx.False(Directory.Exists(finished));
        AssertEx.True(Directory.Exists(live), "the run that IS executing is the one that must survive.");
    }

    /// <summary>
    ///     The registry is read per call, not sampled once when the service is built: a run that starts between the
    ///     node's startup and this delete is exactly the case the gate exists for.
    /// </summary>
    [Test]
    public async Task DeleteAsync_WhenTheRunStartsAfterTheServiceWasBuilt_StillRefuses()
    {
        var run = SeedRun(Now.AddDays(-1));
        var executing = new AgentHomeRunExecutionRegistry();
        var service = Service(executingRuns: executing);

        using var scope = executing.Begin(Path.GetFileName(run));
        var outcome = await service.DeleteAsync(Path.GetFileName(run));

        AssertEx.Equal(AgentHomeRunDeleteOutcome.Conflict, outcome,
            "the registry must be read on the way to the removal, not captured when the service is constructed.");
        AssertEx.True(Directory.Exists(run));
    }

    /// <summary>
    ///     The apply guard, not the executing registry: the two gates answer for different halves of a run's life.
    ///     Held through the real guard the node registers — a double would prove only that the test can lie to it.
    /// </summary>
    [Test]
    public async Task DeleteAsync_WhileThisRunsPatchIsBeingApplied_AnswersConflictAndLeavesTheRunIntact()
    {
        var run = SeedRun(Now.AddDays(-1));
        var guard = new AgentHomeRunApplyGuard();

        using (guard.BeginApply(Path.GetFileName(run)))
        {
            var refused = await Service(applyGuard: guard).DeleteAsync(Path.GetFileName(run));

            AssertEx.Equal(AgentHomeRunDeleteOutcome.Conflict, refused,
                "the apply writes its outcome into this directory at the end; pulling it out mid-apply loses the "
                + "only record of files that really were written to the operator's folders.");
            AssertEx.True(Directory.Exists(run));
            AssertEx.True(File.Exists(Path.Combine(run, "logs", "events.jsonl")), "the run's log must still be there.");
        }

        AssertEx.Equal(AgentHomeRunDeleteOutcome.Deleted, await Service(applyGuard: guard).DeleteAsync(Path.GetFileName(run)),
            "once the apply is done the run is an ordinary one again.");
    }

    /// <summary>
    ///     An apply of a DIFFERENT run is not this run's business. The guard is keyed per run id precisely so one
    ///     apply does not freeze the whole run history the way the sandbox-wide execution lease would.
    /// </summary>
    [Test]
    public async Task DeleteAsync_WhileAnotherRunsPatchIsBeingApplied_StillDeletes()
    {
        var run = SeedRun(Now.AddDays(-2));
        var applying = SeedRun(Now.AddDays(-1));
        var guard = new AgentHomeRunApplyGuard();

        using var scope = guard.BeginApply(Path.GetFileName(applying));
        var outcome = await Service(applyGuard: guard).DeleteAsync(Path.GetFileName(run));

        AssertEx.Equal(AgentHomeRunDeleteOutcome.Deleted, outcome);
        AssertEx.True(Directory.Exists(applying), "the run being applied is the one that must survive.");
    }

    /// <summary>
    ///     The disk-error half of the 409: the same answer as "in flight", because the operator's next move is the
    ///     same and naming the error would say where the run sits. A real unwritable directory, not a thrown double.
    /// </summary>
    [Test]
    [ExcludeOn(OS.Windows)]
    [UnsupportedOSPlatform("windows")]
    public async Task DeleteAsync_WhenTheRemovalItselfFails_AnswersConflictWithoutNamingTheError()
    {
        if (Environment.IsPrivilegedProcess)
        {
            Skip.Test("BLOCKED: a privileged process bypasses the directory permission this test denies the removal with.");
        }

        var run = SeedRun(Now.AddDays(-1));
        var logger = new RecordingLogger<AgentHomeRunDeleteService>();
        var service = new AgentHomeRunDeleteService(Options.Create(AgentHomeOptions()),
            new FakeNodeDataDirectory(_dataRoot.Path),
            new AgentHomeRunExecutionRegistry(),
            new AgentHomeRunApplyGuard(),
            logger);

        // Readable and traversable, so every gate before the removal still passes, but nothing inside may be
        // unlinked — so the recursive delete fails on its first child and the run is left whole.
        File.SetUnixFileMode(run, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var outcome = await service.DeleteAsync(Path.GetFileName(run));

            AssertEx.Equal(AgentHomeRunDeleteOutcome.Conflict, outcome);
            AssertEx.True(Directory.Exists(run), "a removal that failed must leave the run exactly as it was.");
            AssertEx.True(logger.HasEntry(LogLevel.Warning, "could not be deleted"),
                "the failure is the node's to diagnose, so it goes to the log rather than into the operator's answer.");
        }
        finally
        {
            // Restored so the temp directory can be cleaned up at the end of the test.
            File.SetUnixFileMode(run, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private AgentHomeRunDeleteService Service(AgentHomeRunExecutionRegistry? executingRuns = null,
        AgentHomeRunApplyGuard? applyGuard = null) =>
        new(Options.Create(AgentHomeOptions()),
            new FakeNodeDataDirectory(_dataRoot.Path),
            executingRuns ?? new AgentHomeRunExecutionRegistry(),
            applyGuard ?? new AgentHomeRunApplyGuard(),
            new RecordingLogger<AgentHomeRunDeleteService>());

    private AgentHomeOptions AgentHomeOptions() =>
        new()
        {
            Enabled = true,
            RootPath = Path.Combine(_dataRoot.Path, "agent-home-state")
        };

    private string RunsRoot()
    {
        var root = Path.Combine(_dataRoot.Path, "agent-home-state", "agent-home", "runs");
        Directory.CreateDirectory(root);
        return root;
    }

    private static string RunId(DateTimeOffset startedAt, int counter) =>
        string.Create(CultureInfo.InvariantCulture, $"run-{startedAt.ToUnixTimeMilliseconds()}-{counter}");

    private string SeedRun(DateTimeOffset startedAt)
    {
        var directory = Path.Combine(RunsRoot(), RunId(startedAt, ++_counter));
        Directory.CreateDirectory(Path.Combine(directory, "logs"));
        File.WriteAllText(Path.Combine(directory, "logs", "events.jsonl"), "{\"eventName\":\"started\"}\n");
        return directory;
    }
}
