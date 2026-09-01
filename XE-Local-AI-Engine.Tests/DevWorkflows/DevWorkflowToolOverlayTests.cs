namespace XE_Local_AI_Engine.Tests.DevWorkflows;

using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;
using XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Tests.Development;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     What a materialized child's validation node judges. Live, it judged the committed base: Dev Mode leaves the
///     child's implementation as a STAGED patch in the attempt's own worktree, and this node prepares a fresh clone of
///     <c>refs/heads/{baseBranch}</c> — so the per-slice quality gate reported green about a tree the child's change was
///     not in, and the <c>retryTarget</c> fix loop could never fire on a real implementation failure.
///     <para>
///         Driven against a REAL git repository, because the overlay is a real <c>git apply --index</c> and a fake of it
///         would be a test of the arrangement rather than of the mechanism.
///     </para>
/// </summary>
public sealed class DevWorkflowToolOverlayTests : IDisposable
{
    private const string ApprovedSubject = "SUBJECT-HASH";

    private const string PatchHash = "PATCH-HASH";

    /// <summary>One clone group: the child's implementation and the validation that judges it.</summary>
    private const string CloneGraph = """
                                      {
                                        "schemaVersion": 1,
                                        "nodes": [
                                          { "nodeKey": "implement#alpha", "nodeType": "DevTask", "label": "Implement", "nodeTimeoutSeconds": 900 },
                                          { "nodeKey": "validate#alpha", "nodeType": "Tool", "label": "Validate" }
                                        ],
                                        "edges": [{ "from": "implement#alpha", "to": "validate#alpha" }]
                                      }
                                      """;

    /// <summary>A clone group that offers TWO implementations upstream of the same validation.</summary>
    private const string TwoImplementationGraph = """
                                                  {
                                                    "schemaVersion": 1,
                                                    "nodes": [
                                                      { "nodeKey": "implement#alpha", "nodeType": "DevTask", "label": "Implement", "nodeTimeoutSeconds": 900 },
                                                      { "nodeKey": "implement#beta", "nodeType": "DevTask", "label": "Implement too", "nodeTimeoutSeconds": 900 },
                                                      { "nodeKey": "validate#alpha", "nodeType": "Tool", "label": "Validate" }
                                                    ],
                                                    "edges": [
                                                      { "from": "implement#alpha", "to": "implement#beta" },
                                                      { "from": "implement#beta", "to": "validate#alpha" }
                                                    ]
                                                  }
                                                  """;

    /// <summary>What the child implemented: one added file, as a plain unified diff.</summary>
    private const string Patch = """
                                 diff --git a/subtract.txt b/subtract.txt
                                 new file mode 100644
                                 --- /dev/null
                                 +++ b/subtract.txt
                                 @@ -0,0 +1 @@
                                 +the child's own work

                                 """;

    private static readonly Guid OriginNodeRunId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static readonly Guid ProjectId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static readonly Guid RunId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static readonly Guid TaskId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "xe-devflow-overlay-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort test cleanup.
        }
    }

    /// <summary>
    ///     The fix itself: the sibling implementation's approved patch is in the workspace, staged, before a single
    ///     validation command runs — and the report says which patch it was, so the evidence names what was judged.
    /// </summary>
    [Test]
    public async Task AMaterializedChildsValidation_OverlaysTheSiblingImplementationsApprovedPatch()
    {
        var workspace = await WorkspaceAsync().ConfigureAwait(false);
        var commands = Commands(DevTask(DevelopmentTaskStatus.AwaitingApply), Artifact(ApprovedSubject));

        var overlay = await commands.OverlayAsync(Run(), ValidateRow(), Session(workspace), CancellationToken.None).ConfigureAwait(false);

        AssertEx.Null(overlay.Refusal);
        var basedOn = AssertEx.NotNull(overlay.BasedOn);
        AssertEx.Equal(TaskId, basedOn.DevelopmentTaskId);
        AssertEx.Equal(PatchHash, basedOn.PatchHash);
        AssertEx.True(File.Exists(Path.Combine(workspace, "subtract.txt")), "the commands are about to run over the child's work rather than over the base.");
        AssertEx.Contains(await StatusAsync(workspace).ConfigureAwait(false),
            "A  subtract.txt",
            message: "staged, exactly as the apply gate stages it — the validation commands see an index that matches the approved subject.");
    }

    /// <summary>
    ///     A stored patch that is not the subject the task's approval names is not judged quietly against the base: it
    ///     refuses the node. Validating the base and reporting green would be the same silent lie the fix removes.
    /// </summary>
    [Test]
    public async Task WhenTheStoredPatchIsNotTheApprovedSubject_TheNodeRefusesRatherThanJudgingTheBase()
    {
        var workspace = await WorkspaceAsync().ConfigureAwait(false);
        var commands = Commands(DevTask(DevelopmentTaskStatus.AwaitingApply), Artifact("A-DIFFERENT-SUBJECT"));

        var overlay = await commands.OverlayAsync(Run(), ValidateRow(), Session(workspace), CancellationToken.None).ConfigureAwait(false);

        AssertEx.Null(overlay.BasedOn);
        AssertEx.Contains(AssertEx.NotNull(overlay.Refusal), "not the subject its approval names");
        AssertEx.False(File.Exists(Path.Combine(workspace, "subtract.txt")), "and nothing was written into the workspace on the way to refusing.");
    }

    /// <summary>
    ///     A validation node runs only after its implementation SUCCEEDED, so a sibling with no approved patch is an
    ///     anomaly — not a licence to validate the base and report green about it. It refuses, naming the state.
    /// </summary>
    [Test]
    public async Task WhenTheSiblingHasNoApprovedPatchYet_TheNodeRefusesRatherThanJudgingTheBase()
    {
        var workspace = await WorkspaceAsync().ConfigureAwait(false);
        var commands = Commands(DevTask(DevelopmentTaskStatus.InProgress), Artifact(ApprovedSubject));

        var overlay = await commands.OverlayAsync(Run(), ValidateRow(), Session(workspace), CancellationToken.None).ConfigureAwait(false);

        AssertEx.Null(overlay.BasedOn);
        AssertEx.Contains(AssertEx.NotNull(overlay.Refusal), "InProgress");
        AssertEx.Contains(AssertEx.NotNull(overlay.Refusal), "runs only after that implementation succeeded");
        AssertEx.False(File.Exists(Path.Combine(workspace, "subtract.txt")));
    }

    /// <summary>
    ///     A task that reached <c>AwaitingApply</c> through the generic transition can carry no approved subject at all,
    ///     and then nothing binds its stored patch to an approval. Structurally the same hole as a subject MISMATCH, so
    ///     it gets the same answer rather than an unbound overlay.
    /// </summary>
    [Test]
    public async Task WhenTheApprovedTaskCarriesNoSubjectAtAll_TheNodeRefusesInsteadOfOverlayingUnbound()
    {
        var workspace = await WorkspaceAsync().ConfigureAwait(false);
        var commands = Commands(DevTask(DevelopmentTaskStatus.AwaitingApply, approvedSubjectHash: null), Artifact(ApprovedSubject));

        var overlay = await commands.OverlayAsync(Run(), ValidateRow(), Session(workspace), CancellationToken.None).ConfigureAwait(false);

        AssertEx.Null(overlay.BasedOn);
        AssertEx.Contains(AssertEx.NotNull(overlay.Refusal), "names no approved subject");
        AssertEx.False(File.Exists(Path.Combine(workspace, "subtract.txt")), "an unbound patch is not applied on the way to refusing.");
    }

    /// <summary>
    ///     Two implementations upstream of one validation in the same clone group: which work the quality gate is ABOUT
    ///     is then ambiguous, and picking one alphabetically would answer a question nobody asked. It refuses, naming
    ///     both, because this is an authoring or materialization fault a human has to look at.
    /// </summary>
    [Test]
    public async Task WhenTheCloneGroupOffersTwoImplementations_TheNodeRefusesAndNamesThem()
    {
        var workspace = await WorkspaceAsync().ConfigureAwait(false);
        var commands = Commands(DevTask(DevelopmentTaskStatus.AwaitingApply), Artifact(ApprovedSubject), twoImplementations: true);

        var overlay = await commands.OverlayAsync(Run(TwoImplementationGraph), ValidateRow(), Session(workspace), CancellationToken.None).ConfigureAwait(false);

        AssertEx.Null(overlay.BasedOn);
        var refusal = AssertEx.NotNull(overlay.Refusal);
        AssertEx.Contains(refusal, "'implement#alpha'");
        AssertEx.Contains(refusal, "'implement#beta'");
        AssertEx.False(File.Exists(Path.Combine(workspace, "subtract.txt")));
    }

    /// <summary>
    ///     The patch no longer applies — the base moved under it, or the tree already carries a conflicting change. The
    ///     node refuses rather than running its commands over whatever the workspace happens to hold.
    /// </summary>
    [Test]
    public async Task WhenTheApprovedPatchNoLongerApplies_TheNodeRefusesRatherThanJudgingWhatIsThere()
    {
        var workspace = await WorkspaceAsync().ConfigureAwait(false);

        // The same path the patch adds, already committed with different content: git apply refuses, exactly as it does
        // when the base branch moved on after the patch was produced.
        await File.WriteAllTextAsync(Path.Combine(workspace, "subtract.txt"), "someone else's work\n").ConfigureAwait(false);
        await DevelopmentMountBrokerTests.RunGitAsync(workspace, "add", "subtract.txt").ConfigureAwait(false);
        await DevelopmentMountBrokerTests.RunGitAsync(workspace, "commit", "-m", "conflicting change").ConfigureAwait(false);

        var commands = Commands(DevTask(DevelopmentTaskStatus.AwaitingApply), Artifact(ApprovedSubject));

        var overlay = await commands.OverlayAsync(Run(), ValidateRow(), Session(workspace), CancellationToken.None).ConfigureAwait(false);

        AssertEx.Null(overlay.BasedOn);
        AssertEx.Contains(AssertEx.NotNull(overlay.Refusal), "did not apply to this node's freshly prepared workspace");
        AssertEx.Equal("someone else's work\n", await File.ReadAllTextAsync(Path.Combine(workspace, "subtract.txt")).ConfigureAwait(false));
    }

    /// <summary>
    ///     A Tool node that is not a materialized clone has no sibling implementation and validates the base, which is
    ///     the recorded v1 ceiling for the integration-stage <c>fullvalidate</c> node.
    /// </summary>
    [Test]
    public async Task ANodeRunThatIsNotAClone_HasNoSiblingAndOverlaysNothing()
    {
        var workspace = await WorkspaceAsync().ConfigureAwait(false);
        var commands = Commands(DevTask(DevelopmentTaskStatus.AwaitingApply), Artifact(ApprovedSubject));
        var standalone = ValidateRow() with
        {
            MaterializedFromNodeRunId = null,
            MaterializationIndex = null
        };

        var overlay = await commands.OverlayAsync(Run(), standalone, Session(workspace), CancellationToken.None).ConfigureAwait(false);

        AssertEx.Null(overlay.Refusal);
        AssertEx.Null(overlay.BasedOn);
        AssertEx.False(File.Exists(Path.Combine(workspace, "subtract.txt")));
    }

    /// <summary>
    ///     The security-relevant half: bytes that do not verify against the artifact row's own digest are not applied
    ///     and are not quietly skipped either. The node refuses, because a green report over the base would be a
    ///     quality gate that passed without judging anything.
    /// </summary>
    [Test]
    public async Task WhenTheApprovedPatchDoesNotVerify_TheNodeRefusesRatherThanJudgingTheBase()
    {
        var workspace = await WorkspaceAsync().ConfigureAwait(false);
        var commands = Commands(DevTask(DevelopmentTaskStatus.AwaitingApply), patch: null);

        var overlay = await commands.OverlayAsync(Run(), ValidateRow(), Session(workspace), CancellationToken.None).ConfigureAwait(false);

        AssertEx.Null(overlay.BasedOn);
        AssertEx.Contains(AssertEx.NotNull(overlay.Refusal), "could not be verified");
        AssertEx.False(File.Exists(Path.Combine(workspace, "subtract.txt")));
    }

    private static DevWorkflowToolCommands Commands(DevelopmentTaskSnapshot task,
        DevelopmentArtifactSnapshot? patch,
        bool twoImplementations = false)
    {
        var development = Substitute.For<IDevelopmentStore>();
        _ = development.GetTaskAsync(TaskId, Arg.Any<CancellationToken>()).Returns(task);

        var workflows = Substitute.For<IDevWorkflowStore>();
        _ = workflows.ListNodeRunsAsync(RunId, Arg.Any<CancellationToken>())
                     .Returns(twoImplementations
                         ? [ImplementRow(), Row("implement#beta", DevWorkflowNodeType.DevTask, TaskId), ValidateRow()]
                         : [ImplementRow(), ValidateRow()]);

        return new DevWorkflowToolCommands(development,
            Substitute.For<IDevelopmentRepositoryBindingService>(),
            Substitute.For<IDevelopmentSandboxRuntimeProvider>(),
            new StubEvidenceService(patch),
            workflows,
            new DevWorkflowGraphCache(),
            Options.Create(new DevelopmentOptions()),
            Options.Create(new DevWorkflowOptions()),
            TimeProvider.System,
            Substitute.For<IServiceProvider>());
    }

    /// <summary>A real repository with a base commit, standing in for the prepared clone of the base branch.</summary>
    private async Task<string> WorkspaceAsync()
    {
        var workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(workspace);
        await DevelopmentMountBrokerTests.RunGitAsync(workspace, "init", "--initial-branch=main", ".").ConfigureAwait(false);
        await DevelopmentMountBrokerTests.RunGitAsync(workspace, "config", "user.email", "overlay@example.invalid").ConfigureAwait(false);
        await DevelopmentMountBrokerTests.RunGitAsync(workspace, "config", "user.name", "Overlay Test").ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(workspace, "README.md"), "base\n").ConfigureAwait(false);
        await DevelopmentMountBrokerTests.RunGitAsync(workspace, "add", "README.md").ConfigureAwait(false);
        await DevelopmentMountBrokerTests.RunGitAsync(workspace, "commit", "-m", "base").ConfigureAwait(false);
        return workspace;
    }

    private DevelopmentWorkspaceSession Session(string workspace)
    {
        var runtime = Path.Combine(_root, "runtime");
        Directory.CreateDirectory(runtime);
        return new DevelopmentWorkspaceSession(ProjectId,
            TaskId,
            AttemptId: Guid.NewGuid(),
            "BASECOMMIT",
            "IDENTITY",
            workspace,
            runtime,
            new SandboxHandle
            {
                ProviderName = "test",
                SandboxId = "sandbox-1",
                AttachKey = new SandboxAttachKey
                {
                    OwnerUserId = "owner",
                    NodeId = "node",
                    ProviderName = "test",
                    RuntimeProfile = "development-local",
                    ManifestVersion = 1
                },
                CreatedAt = DateTimeOffset.UnixEpoch,
                ManifestVersion = 1
            });
    }

    private static async Task<string> StatusAsync(string workspace)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workspace,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("status");
        startInfo.ArgumentList.Add("--porcelain=v1");
        using var process = new Process
        {
            StartInfo = startInfo
        };
        _ = process.Start();
        var output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);
        return output;
    }

    private static DevelopmentTaskSnapshot DevTask(DevelopmentTaskStatus status, string? approvedSubjectHash = ApprovedSubject) =>
        new(TaskId,
            ProjectId,
            "Add Subtract",
            "Add the missing operation.",
            "[]",
            status,
            CurrentReviewRound: 1,
            MaxReviewRounds: 2,
            BlockedReason: null,
            BlockedAtUtc: null,
            status == DevelopmentTaskStatus.InProgress ? null : approvedSubjectHash,
            CreatedAtUtc: 1,
            UpdatedAtUtc: 2,
            Version: 3);

    private static DevelopmentArtifactSnapshot Artifact(string subjectHash) =>
        new(Guid.Parse("55555555-5555-5555-5555-555555555555"),
            ProjectId,
            TaskId,
            AttemptId: Guid.NewGuid(),
            XE_Local_AI_Engine.Client.Persistence.Entities.DevelopmentArtifactKind.Patch,
            SchemaVersion: 1,
            $"{ProjectId:N}/55555555555555555555555555555555",
            PatchHash,
            Encoding.UTF8.GetByteCount(Patch),
            CreatedAtUtc: 4,
            "BASECOMMIT",
            subjectHash,
            "MANIFEST-HASH",
            InputArtifactIdsJson: null,
            "development-workspace-v1",
            IsValid: true,
            "PROFILE-DIGEST");

    private static DevWorkflowRunSnapshot Run(string graphJson = CloneGraph) =>
        new(RunId,
            WorkItemId: Guid.NewGuid(),
            DefinitionId: Guid.NewGuid(),
            DefinitionVersion: 1,
            "graph-hash",
            graphJson,
            GraphRevision: 2,
            DevWorkflowRunStatus.Running,
            LastSequence: 9,
            FailureClass: null,
            TerminalReason: null,
            StartedAtUtc: 1,
            EndedAtUtc: null,
            CreatedAtUtc: 1,
            UpdatedAtUtc: 2,
            Version: 3);

    private static DevWorkflowNodeRunSnapshot ImplementRow() =>
        Row("implement#alpha", DevWorkflowNodeType.DevTask, TaskId);

    private static DevWorkflowNodeRunSnapshot ValidateRow() =>
        Row("validate#alpha", DevWorkflowNodeType.Tool, developmentTaskId: null);

    private static DevWorkflowNodeRunSnapshot Row(string nodeKey, DevWorkflowNodeType nodeType, Guid? developmentTaskId) =>
        new(Guid.NewGuid(),
            RunId,
            nodeKey,
            nodeType,
            Attempt: 1,
            MaxAttempts: 3,
            SessionResumes: 0,
            DevWorkflowNodeRunStatus.Running,
            QueueReason: null,
            PendingDecisionKind: null,
            Sequence: 5,
            WorkSessionId: null,
            WorkSessionAvailable: false,
            AgentDefinitionId: null,
            ProjectId,
            developmentTaskId,
            InputJson: null,
            OutputJson: null,
            PolicyResolutionJson: null,
            OriginNodeRunId,
            MaterializationIndex: 1,
            FailureClass: null,
            TerminalReason: null,
            QueuedAtUtc: null,
            StartedAtUtc: 6,
            EndedAtUtc: null,
            CreatedAtUtc: 5);

    /// <summary>
    ///     The evidence service is internal, so it is stubbed by hand rather than proxied. Only the one method this
    ///     path calls answers; a null artifact stands for a read that failed its immutable verification, which is the
    ///     exact-hash check the trusted apply port makes on the same bytes.
    /// </summary>
    private sealed class StubEvidenceService(DevelopmentArtifactSnapshot? patch) : IDevelopmentEvidenceService
    {
        public Task<DevelopmentEvidenceSet> ResolveCurrentAsync(Guid taskId,
            DevelopmentWorkspaceSession session,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DevelopmentArtifactWith<ReadOnlyMemory<byte>>> ReadLatestAsync(Guid taskId,
            XE_Local_AI_Engine.Client.Persistence.Entities.DevelopmentArtifactKind kind,
            CancellationToken cancellationToken = default) =>
            patch is null
                ? throw new DevelopmentInvalidTransitionException("The Patch artifact failed immutable blob verification (HashMismatch).")
                : Task.FromResult(new DevelopmentArtifactWith<ReadOnlyMemory<byte>>(patch, Encoding.UTF8.GetBytes(Patch)));

        public Task InvalidateApprovalEvidenceAsync(Guid taskId, string sanitizedReason, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DevelopmentPreparedArtifact> PrepareAsync(DevelopmentExecutionSnapshot snapshot,
            XE_Local_AI_Engine.Client.Persistence.Entities.DevelopmentArtifactKind kind,
            ReadOnlyMemory<byte> content,
            DevelopmentPatchEvidence evidence,
            IReadOnlyList<Guid> inputArtifactIds,
            string commandProfileVersion,
            string commandProfileDigest,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
