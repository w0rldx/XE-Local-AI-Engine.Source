namespace XE_Local_AI_Engine.Tests.Development;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Client.Services.Development.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using PersistenceAttemptStatus = XE_Local_AI_Engine.Client.Persistence.Entities.DevelopmentAttemptStatus;

/// <summary>
///     The Development Mode workspace-retention sweep against a real filesystem the test owns. Every "kept" case
///     sweeps beside a reclaimable control, so a sweep that does nothing fails it too.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class DevelopmentWorkspaceRetentionServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid ProjectId = Guid.Parse("4b1f0c4e-7d0a-4f43-9a51-2a8c3f6d9e10");

    private readonly Dictionary<Guid, IReadOnlyList<DevelopmentAttemptSnapshot>> _attempts = [];
    private readonly TempDirectory _dataRoot = new("xe-dev-workspace-retention");
    private readonly List<DevelopmentTaskSnapshot> _tasks = [];

    public void Dispose() =>
        _dataRoot.Dispose();

    [Test]
    [Arguments(DevelopmentTaskStatus.Completed)]
    [Arguments(DevelopmentTaskStatus.Cancelled)]
    public async Task Sweep_TerminalTaskPastTheRetentionAge_DeletesBothDirectories(DevelopmentTaskStatus status)
    {
        var finished = SeedTask(status, TimeSpan.FromDays(8));

        await SweepAsync();

        AssertGone(finished);
    }

    [Test]
    public async Task Sweep_CompletedTaskWithinTheRetentionAge_KeepsItsDirectories()
    {
        var control = SeedTask(DevelopmentTaskStatus.Completed, TimeSpan.FromDays(8));
        var young = SeedTask(DevelopmentTaskStatus.Completed, TimeSpan.FromDays(6));

        await SweepAsync();

        AssertGone(control);
        AssertKept(young, "a finished task stays reclaimable-but-kept until the retention age passes.");
    }

    [Test]
    [Arguments(DevelopmentTaskStatus.Blocked)]
    [Arguments(DevelopmentTaskStatus.ChangesRequested)]
    [Arguments(DevelopmentTaskStatus.AwaitingApply)]
    public async Task Sweep_NonTerminalTaskPastTheRetentionAge_KeepsItsDirectories(DevelopmentTaskStatus status)
    {
        var control = SeedTask(DevelopmentTaskStatus.Completed, TimeSpan.FromDays(8));
        var resumable = SeedTask(status, TimeSpan.FromDays(30));

        await SweepAsync();

        AssertGone(control);
        AssertKept(resumable, "a retried or resumed task reuses its workspace (ADR 0001), however old it is.");
    }

    [Test]
    [Arguments(PersistenceAttemptStatus.Running)]
    [Arguments(PersistenceAttemptStatus.Pending)]
    public async Task Sweep_CompletedTaskWithALiveAttempt_KeepsItsDirectories(PersistenceAttemptStatus attemptStatus)
    {
        var control = SeedTask(DevelopmentTaskStatus.Completed, TimeSpan.FromDays(8));
        var live = SeedTask(DevelopmentTaskStatus.Completed, TimeSpan.FromDays(8));
        _attempts[live.TaskId] = [Attempt(live.TaskId, attemptStatus)];

        await SweepAsync();

        AssertGone(control);
        AssertKept(live, "a workspace an attempt is still running in must never be deleted under it.");
    }

    [Test]
    public async Task Sweep_OrphanDirectoryPastTheGracePeriod_IsDeleted()
    {
        var orphan = SeedTask(DevelopmentTaskStatus.Completed, TimeSpan.FromHours(1), withRow: false);

        await SweepAsync();

        AssertGone(orphan);
    }

    [Test]
    public async Task Sweep_OrphanDirectoryWithinTheGracePeriod_IsKept()
    {
        var control = SeedTask(DevelopmentTaskStatus.Completed, TimeSpan.FromDays(8));
        var fresh = SeedTask(DevelopmentTaskStatus.Completed, TimeSpan.FromMinutes(5), withRow: false);

        await SweepAsync();

        AssertGone(control);
        AssertKept(fresh, "a directory whose task row is not visible yet may belong to a task being created right now.");
    }

    [Test]
    public async Task Sweep_StalePartialClone_IsDeletedAndAFreshOneKept()
    {
        var stale = SeedPartial(Guid.NewGuid(), TimeSpan.FromHours(1));
        var fresh = SeedPartial(Guid.NewGuid(), TimeSpan.FromMinutes(5));

        await SweepAsync();

        AssertEx.False(Directory.Exists(stale), "a partial first clone past the grace period is an interrupted clone.");
        AssertEx.True(Directory.Exists(fresh), "a partial clone within the grace period may still be cloning.");
    }

    [Test]
    public async Task Sweep_StalePartialCloneOfANonTerminalTask_IsKept()
    {
        var control = SeedPartial(Guid.NewGuid(), TimeSpan.FromHours(1));
        var activeTaskId = Guid.NewGuid();
        _tasks.Add(TaskRow(activeTaskId, DevelopmentTaskStatus.InProgress, TimeSpan.FromHours(1)));
        var cloning = SeedPartial(activeTaskId, TimeSpan.FromHours(1));

        await SweepAsync();

        AssertEx.False(Directory.Exists(control), "the orphan partial beside it proves the sweep ran.");
        AssertEx.True(Directory.Exists(cloning), "a live task's partial may still be cloning; its own next prepare removes it.");
    }

    [Test]
    public async Task Sweep_WhenTheRuntimeRootIsALink_RefusesTheWholeSweep()
    {
        SymlinkSupport.EnsureSupported();

        var finished = SeedTask(DevelopmentTaskStatus.Completed, TimeSpan.FromDays(8));
        var runtimeRoot = Path.Combine(_dataRoot.Path, "development", "runtime");
        var outside = Path.Combine(_dataRoot.Path, "outside");
        Directory.Move(runtimeRoot, outside);
        Directory.CreateSymbolicLink(runtimeRoot, outside);
        var logger = new RecordingLogger<DevelopmentWorkspaceRetentionService>();

        await SweepAsync(logger);

        AssertEx.True(Directory.Exists(finished.Workspace), "a linked root would carry the counterpart delete through the link.");
        AssertEx.True(Directory.Exists(Path.Combine(outside, ProjectId.ToString("N"), finished.TaskId.ToString("N"))),
            "nothing behind the linked root may be deleted.");
        AssertEx.True(logger.HasEntry(LogLevel.Warning, "is a link"), "a refused sweep says why, once.");
    }

    [Test]
    public async Task Sweep_DirectoryNotMintedByTheNode_IsUntouched()
    {
        var control = SeedTask(DevelopmentTaskStatus.Completed, TimeSpan.FromDays(8));
        var foreign = Path.Combine(_dataRoot.Path, "development", "workspaces", ProjectId.ToString("N"), "not-a-task");
        Directory.CreateDirectory(foreign);
        Directory.SetLastWriteTimeUtc(foreign, Now.AddDays(-30).UtcDateTime);

        await SweepAsync();

        AssertGone(control);
        AssertEx.True(Directory.Exists(foreign), "a name the node did not mint is not the sweep's to delete.");
    }

    [Test]
    public async Task Sweep_TaskDirectoryThatIsALink_IsUntouchedAndItsTargetSurvives()
    {
        SymlinkSupport.EnsureSupported();

        var control = SeedTask(DevelopmentTaskStatus.Completed, TimeSpan.FromDays(8));
        var outside = Path.Combine(_dataRoot.Path, "outside");
        Directory.CreateDirectory(outside);
        var treasure = Path.Combine(outside, "keep.txt");
        await File.WriteAllTextAsync(treasure, "must survive");

        var linkedTaskId = Guid.NewGuid();
        var linked = Path.Combine(_dataRoot.Path, "development", "workspaces", ProjectId.ToString("N"), linkedTaskId.ToString("N"));
        Directory.CreateSymbolicLink(linked, outside);
        _tasks.Add(TaskRow(linkedTaskId, DevelopmentTaskStatus.Completed, TimeSpan.FromDays(8)));

        await SweepAsync();

        AssertGone(control);
        AssertEx.True(File.Exists(treasure), "nothing the sweep does may reach outside the data root through a link.");
        AssertEx.True(Directory.Exists(linked), "a task directory that is itself a link is left alone.");
    }

    [Test]
    public async Task Sweep_WhenTheDevelopmentDirectoryIsALink_RefusesTheWholeSweep()
    {
        SymlinkSupport.EnsureSupported();

        var finished = SeedTask(DevelopmentTaskStatus.Completed, TimeSpan.FromDays(8));
        var development = Path.Combine(_dataRoot.Path, "development");
        var outside = Path.Combine(_dataRoot.Path, "outside");
        Directory.Move(development, outside);
        Directory.CreateSymbolicLink(development, outside);
        var logger = new RecordingLogger<DevelopmentWorkspaceRetentionService>();

        await SweepAsync(logger);

        var task = Path.Combine(ProjectId.ToString("N"), finished.TaskId.ToString("N"));
        AssertEx.True(Directory.Exists(Path.Combine(outside, "workspaces", task)), "nothing behind a linked ancestor may be deleted.");
        AssertEx.True(Directory.Exists(Path.Combine(outside, "runtime", task)), "nothing behind a linked ancestor may be deleted.");
        AssertEx.True(logger.HasEntry(LogLevel.Warning, "is a link"), "a refused sweep says why, once.");
    }

    private async Task SweepAsync(RecordingLogger<DevelopmentWorkspaceRetentionService>? logger = null)
    {
        var store = Substitute.For<IDevelopmentStore>();
        store.ListProjectsAsync(Arg.Any<CancellationToken>()).Returns([Project()]);
        store.ListTasksAsync(ProjectId, Arg.Any<CancellationToken>()).Returns(_ => _tasks.ToList());
        store.ListAttemptsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
             .Returns(call => _attempts.GetValueOrDefault(call.Arg<Guid>(), []));
        await using var services = new ServiceCollection().AddScoped(_ => store).BuildServiceProvider();
        using var service = new DevelopmentWorkspaceRetentionService(Options.Create(new DevelopmentWorkspaceRetentionOptions()),
            new FakeNodeDataDirectory(_dataRoot.Path),
            services.GetRequiredService<IServiceScopeFactory>(),
            new ManualTimeProvider(Now),
            logger ?? new RecordingLogger<DevelopmentWorkspaceRetentionService>());
        await service.SweepAsync(CancellationToken.None);
    }

    /// <summary>Seeds both directories of one task, a read-only file in the clone, and (optionally) its task row.</summary>
    private SeededTask SeedTask(DevelopmentTaskStatus status, TimeSpan age, bool withRow = true)
    {
        var taskId = Guid.NewGuid();
        var workspace = Path.Combine(_dataRoot.Path, "development", "workspaces", ProjectId.ToString("N"), taskId.ToString("N"));
        var runtime = Path.Combine(_dataRoot.Path, "development", "runtime", ProjectId.ToString("N"), taskId.ToString("N"));
        var pack = Path.Combine(workspace, ".git", "objects", "pack");
        Directory.CreateDirectory(pack);
        var packFile = Path.Combine(pack, "pack-0.pack");
        File.WriteAllText(packFile, "pack");
        File.SetAttributes(packFile, FileAttributes.ReadOnly);
        Directory.CreateDirectory(Path.Combine(runtime, "shadow"));
        File.WriteAllText(Path.Combine(runtime, "workspace.json"), "{}");
        Directory.SetLastWriteTimeUtc(workspace, Now.Subtract(age).UtcDateTime);
        Directory.SetLastWriteTimeUtc(runtime, Now.Subtract(age).UtcDateTime);
        if (withRow)
        {
            _tasks.Add(TaskRow(taskId, status, age));
        }

        return new SeededTask(taskId, workspace, runtime);
    }

    private string SeedPartial(Guid taskId, TimeSpan age)
    {
        var partial = Path.Combine(_dataRoot.Path,
            "development",
            "workspaces",
            ProjectId.ToString("N"),
            taskId.ToString("N") + ".partial-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(partial, ".git"));
        Directory.SetLastWriteTimeUtc(partial, Now.Subtract(age).UtcDateTime);
        return partial;
    }

    private static void AssertGone(SeededTask task)
    {
        AssertEx.False(Directory.Exists(task.Workspace), "the reclaimable task's workspace clone must be deleted.");
        AssertEx.False(Directory.Exists(task.Runtime), "the reclaimable task's runtime directory must be deleted with it.");
    }

    private static void AssertKept(SeededTask task, string why)
    {
        AssertEx.True(Directory.Exists(task.Workspace), why);
        AssertEx.True(Directory.Exists(task.Runtime), why);
    }

    private static DevelopmentTaskSnapshot TaskRow(Guid taskId, DevelopmentTaskStatus status, TimeSpan updatedAgo) =>
        new()
        {
            Id = taskId,
            ProjectId = ProjectId,
            Title = "task",
            Requirements = "requirements",
            AcceptanceCriteriaJson = "[]",
            Status = status,
            CurrentReviewRound = 0,
            MaxReviewRounds = 3,
            BlockedReason = null,
            BlockedAtUtc = null,
            ApprovedSubjectHash = null,
            CreatedAtUtc = Now.Subtract(updatedAgo).ToUnixTimeMilliseconds(),
            UpdatedAtUtc = Now.Subtract(updatedAgo).ToUnixTimeMilliseconds(),
            Version = 1
        };

    private static DevelopmentAttemptSnapshot Attempt(Guid taskId, PersistenceAttemptStatus status) =>
        new()
        {
            Id = Guid.NewGuid(),
            TaskId = taskId,
            PredecessorAttemptId = null,
            Role = DevelopmentAttemptRole.Coder,
            ModelId = "coder-model",
            Provider = "local",
            Status = status,
            StartedAtUtc = 1,
            EndedAtUtc = null,
            TerminalReason = null,
            InputTokens = 0,
            OutputTokens = 0,
            Version = 1
        };

    private static DevelopmentProjectSnapshot Project() =>
        new()
        {
            Id = ProjectId,
            Objective = "objective",
            SelectedFolderId = Guid.NewGuid(),
            RepositoryIdentityHash = "repository-hash",
            BaseBranch = "main",
            Status = DevelopmentProjectStatus.Active,
            EgressPolicy = DevelopmentEgressPolicy.LocalOnly,
            CoderModelId = "coder-model",
            ReviewerModelId = "reviewer-model",
            MaxTokens = null,
            MaxDurationSeconds = null,
            ConfigurationVersion = 1,
            TrustedRepositoryAcknowledged = true,
            TrustedRepositoryPolicyVersion = 1,
            TrustedRepositoryAcknowledgedAtUtc = 1,
            CreatedAtUtc = 1,
            UpdatedAtUtc = 1,
            Version = 1,
            CommandProfileJson = null
        };

    private readonly record struct SeededTask(Guid TaskId, string Workspace, string Runtime);
}
