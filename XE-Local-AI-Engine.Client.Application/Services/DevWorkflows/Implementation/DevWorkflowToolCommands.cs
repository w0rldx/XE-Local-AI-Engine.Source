namespace XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;

using System.Text.Json;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using PersistenceDevelopmentAttemptStatus = XE_Local_AI_Engine.Client.Persistence.Entities.DevelopmentAttemptStatus;

/// <summary>Runs a Tool node's validation commands in a prepared sandbox workspace.</summary>
/// <remarks>
///     It calls the substrate BELOW <c>DevelopmentValidationRunner</c>, which is welded to the Dev Mode task machine,
///     but shares the workspace provider, the command profile, the sanitizer and the verdict — so the gate a workflow
///     node applies is Dev Mode's, not a second one that drifted. Committed credentials are reported through
///     <see cref="IDevelopmentWorkspaceSecretsSink" />. See docs/wiki/25-dev-workflows.md ("The patch overlay").
/// </remarks>
internal sealed class DevWorkflowToolCommands : IDevWorkflowToolCommands
{
    /// <summary>camelCase, matching every other document this product puts on a wire.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>What the synthesized snapshot answers for the two attempt-identity fields.</summary>
    /// <remarks>
    ///     They exist for the cloud role route, which a Tool node never reaches; naming them for what this is beats
    ///     borrowing a model id that would read as a claim about which model ran.
    /// </remarks>
    private const string ExecutorIdentity = "dev-workflow-tool-node";

    /// <summary>What stands in for captured output the report had no room for. See <c>Compose</c>.</summary>
    private const string OutputElided = "(The captured output was too large for one artifact and was left out of this report.)";

    private readonly IDevelopmentRepositoryBindingService _bindings;
    private readonly IDevelopmentStore _development;
    private readonly DevelopmentOptions _developmentOptions;
    private readonly IDevelopmentEvidenceService _evidence;
    private readonly DevWorkflowGraphCache _graphs;
    private readonly DevWorkflowOptions _options;
    private readonly IDevelopmentSandboxRuntimeProvider _sandbox;
    private readonly IServiceProvider _services;
    private readonly TimeProvider _timeProvider;
    private readonly IDevWorkflowStore _workflows;

    public DevWorkflowToolCommands(IDevelopmentStore development,
        IDevelopmentRepositoryBindingService bindings,
        IDevelopmentSandboxRuntimeProvider sandbox,
        IDevelopmentEvidenceService evidence,
        IDevWorkflowStore workflows,
        DevWorkflowGraphCache graphs,
        IOptions<DevelopmentOptions> developmentOptions,
        IOptions<DevWorkflowOptions> options,
        TimeProvider timeProvider,
        IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(developmentOptions);
        ArgumentNullException.ThrowIfNull(options);
        _development = development ?? throw new ArgumentNullException(nameof(development));
        _bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
        _sandbox = sandbox ?? throw new ArgumentNullException(nameof(sandbox));
        _evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
        _workflows = workflows ?? throw new ArgumentNullException(nameof(workflows));
        _graphs = graphs ?? throw new ArgumentNullException(nameof(graphs));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _developmentOptions = developmentOptions.Value;
        _options = options.Value;
    }

    public async Task<DevWorkflowToolRun> RunAsync(DevWorkflowRunSnapshot run,
        DevWorkflowGraphNode node,
        DevWorkflowNodeRunSnapshot nodeRun,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(nodeRun);

        ArgumentNullException.ThrowIfNull(run);

        var secrets = new CollectingWorkspaceSecretsSink();
        try
        {
            return await ExecuteAsync(run, node, nodeRun, secrets, cancellationToken);
        }
        catch (Exception exception) when (exception is DevelopmentRepositoryStateConflictException
                                              or SandboxCapabilityNotSupportedException
                                              or KeyNotFoundException)
        {
            // The node cannot run AS CONFIGURED — a missing project, a repository needing reconnection, a sandbox that
            // cannot hold a trusted workspace. Ahead of the security catch: a repository conflict IS one, more specific.
            return Refused(DevWorkflowFailureClasses.Configuration, Sanitized(exception), secrets);
        }
        catch (DevelopmentWorkspaceSecurityException exception)
        {
            // A protected path, an unacknowledged repository, a worktree moved off its base commit, or unsalvageable
            // evidence. Running the same commands again answers none of them, so the class is the non-retryable one.
            return Refused(DevWorkflowFailureClasses.Policy, Sanitized(exception), secrets);
        }
    }

    /// <summary>One exception's message, fit to be stored on a row and rendered on a wire.</summary>
    /// <remarks>
    ///     Shared with the apply variant. These sentences are the ONE thing this lane surfaces that nothing has
    ///     already redacted: the sandbox interpolates an inner IOException's text, which can carry a host path. No
    ///     protected roots are passed, deliberately — the generic absolute-path patterns fire on any path, and the
    ///     repository may not have resolved. A message the sanitizer REFUSES is replaced wholesale.
    /// </remarks>
    internal static string Sanitized(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        try
        {
            return DevelopmentArtifactSanitizer.SanitizeText(exception.Message);
        }
        catch (DevelopmentWorkspaceSecurityException)
        {
            return "This node run's workspace could not be prepared, and the reason could not be shown safely. The engine log has the detail.";
        }
    }

    private async Task<DevWorkflowToolRun> ExecuteAsync(DevWorkflowRunSnapshot run,
        DevWorkflowGraphNode node,
        DevWorkflowNodeRunSnapshot nodeRun,
        CollectingWorkspaceSecretsSink secrets,
        CancellationToken cancellationToken)
    {
        if (nodeRun.DevelopmentProjectId is not { } projectId)
        {
            // Run start refuses a graph with tool nodes on a work item that names no project, so this is the row that
            // was materialized before such a node existed rather than an ordinary miss.
            return Refused(DevWorkflowFailureClasses.Configuration,
                $"Node run '{nodeRun.NodeKey}' runs repository commands but names no development project to run them against.",
                secrets);
        }

        var project = await _development.GetProjectAsync(projectId, cancellationToken);
        var repository = await _bindings.ResolveProjectAsync(projectId, cancellationToken);
        var profile = DevelopmentCommandProfileCatalog.ResolveStored(project.CommandProfileJson);

        // The node's own list when it names one, the profile's otherwise. Checked BEFORE a workspace is prepared: a
        // typo in a definition should not cost a clone and a warm restore before it is reported.
        var commandIds = node.ValidationCommandIds.Count > 0 ? node.ValidationCommandIds : profile.ValidationCommandIds;
        if (commandIds.FirstOrDefault(id => !profile.Commands.Any(command => string.Equals(command.CommandId, id, StringComparison.Ordinal))) is { } unknown)
        {
            return Refused(DevWorkflowFailureClasses.Configuration,
                $"Node '{node.NodeKey}' asks for validation command '{unknown}', which this repository's command profile does not define.",
                secrets);
        }

        // Constructed with THIS lane's sink rather than resolved: the container's provider reports credentials through
        // a Dev Mode store write that resolves a task row a node run does not have. Everything else still comes from it.
        var workspaces = ActivatorUtilities.CreateInstance<DevelopmentWorkspaceProvider>(_services, secrets);
        var snapshot = Synthesize(project, node, run, nodeRun, repository);
        var session = await workspaces.PrepareAsync(snapshot, repository, cancellationToken);

        // Before a single command runs: a materialized child's validation must judge THAT CHILD'S work, and the work is
        // a staged patch in the Dev Mode attempt's own worktree rather than anything the freshly cloned base contains.
        var overlay = await OverlayAsync(run, nodeRun, session, cancellationToken);
        if (overlay.Refusal is { } refusedOverlay)
        {
            return Refused(DevWorkflowFailureClasses.Policy, refusedOverlay, secrets);
        }

        var tools = new DevelopmentWorkspaceTools(_sandbox, session, Options.Create(_developmentOptions), profile);

        // The commands share ONE deadline, the way the Dev Mode gate does: bounding each command alone lets a
        // four-command profile run for four times the budget it is meant to respect.
        var budgetSeconds = BudgetSeconds(node, project);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(budgetSeconds));
        try
        {
            foreach (var commandId in commandIds)
            {
                _ = await tools.RunCommandAsync(commandId, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The node's own budget, not the drain's, whose cancel propagates because only the lane knows whether a run
            // was cancelled or paused. The commands that DID finish are still evidence and the report names them.
            return Result(timedOutAfterSeconds: budgetSeconds);
        }

        return Result(timedOutAfterSeconds: null);

        DevWorkflowToolRun Result(int? timedOutAfterSeconds)
        {
            var protectedRoots = DevelopmentArtifactSanitizer.ResolveProtectedRoots(repository.RepositoryRoot, session);
            var evidence = tools.CommandEvidence.Select(command => DevelopmentArtifactSanitizer.Sanitize(command, protectedRoots)).ToArray();

            // Evaluated against the list this node actually ran: the verdict's first rule is that every declared command
            // produced evidence, which a node narrowing the profile's list would otherwise fail by construction.
            var verdict = DevelopmentValidationVerdict.Evaluate(profile with
            {
                ValidationCommandIds = commandIds
            }, evidence);

            var tests = evidence.Select(static command => command.TestOutcome).OfType<DevelopmentTestOutcome>().Where(static outcome => outcome.Parsed).ToList();
            var passed = verdict.Passed && timedOutAfterSeconds is null;
            var refusal = timedOutAfterSeconds is null ? DevWorkflowFailureClasses.ToolCommandFailed : DevWorkflowFailureClasses.Timeout;
            var failureClass = passed ? null : refusal;

            return new DevWorkflowToolRun
            {
                Passed = passed,
                FailureClass = failureClass,
                FailureCode = verdict.FailureCode,
                SanitizedReason = timedOutAfterSeconds is { } seconds
                    ? $"This node run did not finish its validation commands within the {seconds} seconds it was given."
                    : verdict.FailureDetail,
                CommandsRun = evidence.Length,
                CommandsFailed = evidence.Count(static command => !command.Completed || command.ExitCode != 0),
                TestsPassed = tests.Count == 0 ? null : tests.Sum(static outcome => outcome.Passed),
                TestsFailed = tests.Count == 0 ? null : tests.Sum(static outcome => outcome.Failed),
                Report = Compose(verdict, profile, session, nodeRun, evidence, overlay.BasedOn),
                SecretPaths = secrets.Paths
            };
        }
    }

    /// <summary>Puts the implementation's OWN work into the freshly prepared workspace before anything judges it.</summary>
    /// <remarks>
    ///     The bytes are the approved patch artifact's, verified as the trusted host apply port verifies them and
    ///     bound to the task's <c>ApprovedSubjectHash</c>; the apply is <c>git apply --index</c> inside the SANDBOX
    ///     workspace only. Every anomaly on this path REFUSES (<c>Policy</c>) rather than falling back to the base,
    ///     a green report over the base being the silent lie the overlay exists to remove.
    ///     See docs/wiki/25-dev-workflows.md ("The patch overlay").
    /// </remarks>
    internal async Task<DevWorkflowOverlay> OverlayAsync(DevWorkflowRunSnapshot run,
        DevWorkflowNodeRunSnapshot nodeRun,
        DevelopmentWorkspaceSession session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        var clone = nodeRun.MaterializedFromNodeRunId is not null && nodeRun.MaterializationIndex is not null;
        var implementations = await UpstreamImplementationsAsync(run, nodeRun, clone, cancellationToken);
        if (implementations.Count == 0)
        {
            return default;
        }

        if (implementations.Count > 1)
        {
            // No alphabetical pick: which implementation this validation judges decides what the quality gate is ABOUT,
            // and a graph offering two of them is an authoring or materialization fault a human has to look at.
            return Refuse($"This validation follows more than one implementation {(clone ? "in its materialization group" : "in this run's graph")} "
                          + $"({string.Join(", ", implementations.Select(static row => $"'{row.NodeKey}'"))}), so which work it should judge is "
                          + "ambiguous. Nothing was applied to this workspace.");
        }

        var taskId = implementations[0].DevelopmentTaskId!.Value;
        var task = await _development.GetTaskAsync(taskId, cancellationToken);
        if (task.Status is not (DevelopmentTaskStatus.AwaitingApply or DevelopmentTaskStatus.Completed))
        {
            return Refuse($"The implementation this validation follows is {task.Status} and has produced no approved patch. This node "
                          + "runs only after that implementation succeeded, so its state is an anomaly rather than a licence to judge "
                          + "the base commit instead.");
        }

        try
        {
            var patch = await _evidence.ReadLatestAsync(taskId, DevelopmentArtifactKind.Patch, cancellationToken);
            if (task.ApprovedSubjectHash is not { } approved)
            {
                // A task that reached this status through the generic transition can carry no subject at all. Nothing
                // then binds the stored patch to an approval, which is the one thing that makes it safe to apply.
                return Refuse($"The implementation task is {task.Status} but names no approved subject, so nothing binds its stored patch "
                              + "to an approval. Nothing was applied to this workspace.");
            }

            if (!string.Equals(approved, patch.Artifact.SubjectHash, StringComparison.OrdinalIgnoreCase))
            {
                return Refuse("The implementation task's stored patch is not the subject its approval names, so this node did not judge it. "
                              + "Nothing was applied to this workspace.");
            }

            var applied = await new HostGitRunner(_developmentOptions.MaxAttemptDurationSeconds)
                .RunAsync(session.HostWorktreePath,
                    AgentHomeGit.Arguments("apply", "--index", "--whitespace=error-all", "-"),
                    cancellationToken,
                    patch.Payload,
                    _developmentOptions.MaxPatchBytes,
                    _developmentOptions.MaxCommandOutputBytes);
            if (applied.ExitCode != 0)
            {
                // Deliberately without git's own stderr: it interpolates workspace paths, and this sentence reaches an
                // operator through the row. The engine log keeps the detail.
                return Refuse("The implementation task's approved patch did not apply to this node's freshly prepared workspace, "
                              + "so nothing was judged. The base branch has most likely moved since that patch was produced.");
            }

            return new DevWorkflowOverlay(new DevWorkflowValidationBasedOn(taskId,
                    patch.Artifact.ContentHash,
                    "These commands ran against the implementation task's approved patch, applied to a fresh clone of the base commit."),
                Refusal: null);
        }
        catch (DevelopmentInvalidTransitionException exception)
        {
            return Refuse($"The implementation task's approved patch could not be verified, so this node judged nothing: {Sanitized(exception)}");
        }
    }

    /// <summary>A refusal: nothing was overlaid, so the node run is answered rather than judged.</summary>
    private static DevWorkflowOverlay Refuse(string sanitizedReason) =>
        new(BasedOn: null, sanitizedReason);

    /// <summary>The implementations this validation node exists to judge, resolved from the GRAPH.</summary>
    /// <remarks>
    ///     Not from whether the node run happens to be a materialization clone. The walk stops at an
    ///     <see cref="DevWorkflowToolMode.Apply" /> Tool node and never looks past one. It answers the whole set
    ///     rather than a first match, more than one implementation feeding one validation being a fault to refuse.
    ///     See docs/wiki/25-dev-workflows.md ("The patch overlay").
    /// </remarks>
    private async Task<IReadOnlyList<DevWorkflowNodeRunSnapshot>> UpstreamImplementationsAsync(DevWorkflowRunSnapshot run,
        DevWorkflowNodeRunSnapshot nodeRun,
        bool clone,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(nodeRun);

        var graph = _graphs.Resolve(run);
        var rows = await _workflows.ListNodeRunsAsync(run.Id, cancellationToken);
        if (clone)
        {
            return
            [
                .. rows.Where(row => row.MaterializedFromNodeRunId == nodeRun.MaterializedFromNodeRunId
                                     && row.MaterializationIndex == nodeRun.MaterializationIndex
                                     && row.NodeType == DevWorkflowNodeType.DevTask
                                     && row.DevelopmentTaskId is not null
                                     && graph.Descendants(row.NodeKey).Contains(nodeRun.NodeKey, StringComparer.Ordinal))
                       .OrderBy(static row => row.NodeKey, StringComparer.Ordinal)
            ];
        }

        var nearest = NearestImplementationKeys(graph, nodeRun.NodeKey);
        return
        [
            .. rows.Where(row => row.NodeType == DevWorkflowNodeType.DevTask
                                 && row.DevelopmentTaskId is not null
                                 && nearest.Contains(row.NodeKey))
                   .OrderBy(static row => row.NodeKey, StringComparer.Ordinal)
        ];
    }

    /// <summary>The <c>DevTask</c> node keys a walk back up the in-edges reaches FIRST.</summary>
    /// <remarks>
    ///     A DevTask ends its branch, so an implementation upstream of another implementation is that one's business.
    ///     An <see cref="DevWorkflowToolMode.Apply" /> node ends its branch too: what it applied is the base.
    /// </remarks>
    private static HashSet<string> NearestImplementationKeys(DevWorkflowGraph graph, string from)
    {
        var nearest = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(from);
        while (pending.Count > 0)
        {
            foreach (var key in graph.InboundEdges(pending.Pop()).Select(static edge => edge.From).Where(seen.Add))
            {
                if (graph.Nodes[key].NodeType == DevWorkflowNodeType.DevTask)
                {
                    _ = nearest.Add(key);
                }
                else if (graph.Nodes[key] is not { NodeType: DevWorkflowNodeType.Tool, ToolMode: DevWorkflowToolMode.Apply })
                {
                    pending.Push(key);
                }
            }
        }

        return nearest;
    }

    /// <summary>The report bytes, bounded so the artifact store can always take them.</summary>
    /// <remarks>
    ///     Each command's captured output is already capped, but a profile carries several of those caps and the
    ///     artifact limit sits below that. A document that will not fit keeps every command's identity, exit code,
    ///     duration and test result and gives up only the captured text, with a line saying so.
    /// </remarks>
    private byte[] Compose(DevelopmentValidationVerdict verdict,
        DevelopmentCommandProfile profile,
        DevelopmentWorkspaceSession session,
        DevWorkflowNodeRunSnapshot nodeRun,
        IReadOnlyList<DevelopmentCommandEvidence> evidence,
        DevWorkflowValidationBasedOn? basedOn)
    {
        var report = new DevWorkflowValidationReport(verdict.Passed,
            nodeRun.NodeKey,
            nodeRun.Attempt,
            session.BaseCommit,
            profile.ProfileId,
            profile.ComputeDigest(),
            verdict.FailureCode,
            verdict.FailureDetail,
            evidence,
            _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            basedOn);
        var composed = JsonSerializer.SerializeToUtf8Bytes(report, JsonOptions);
        if (composed.Length <= _options.MaxArtifactBytes)
        {
            return composed;
        }

        return JsonSerializer.SerializeToUtf8Bytes(report with
        {
            Commands =
            [
                .. evidence.Select(static command => command with
                {
                    StandardOutput = OutputElided,
                    StandardError = OutputElided
                })
            ]
        }, JsonOptions);
    }

    /// <summary>The node's budget, the project's and the hard attempt cap, whichever is smallest.</summary>
    /// <remarks>
    ///     The cap is the outer bound it claims to be, so a node asking for more gets less. This bounds the PASS;
    ///     <see cref="DevWorkflowDeadline" /> bounds the node run, and the two cannot disagree, since this takes the
    ///     smaller number and counts from the earlier instant.
    /// </remarks>
    private int BudgetSeconds(DevWorkflowGraphNode node, DevelopmentProjectSnapshot project) =>
        Math.Min(Math.Min(node.NodeTimeoutSeconds ?? int.MaxValue, project.MaxDurationSeconds ?? int.MaxValue), _developmentOptions.MaxAttemptDurationSeconds);

    /// <summary>The execution snapshot a Tool node-run stands in for a Dev Mode attempt with.</summary>
    /// <remarks>
    ///     Both isolation keys are the ATTEMPT's, not the node run's, so a re-attempt is a real second try rather than
    ///     a re-validation of the first attempt's commit in the first attempt's preserved tree. Deterministic, and the
    ///     same derivation the tick's idempotency keys use, so a replayed poll prepares the same workspace.
    ///     See docs/wiki/25-dev-workflows.md ("The patch overlay").
    /// </remarks>
    internal static DevelopmentExecutionSnapshot Synthesize(DevelopmentProjectSnapshot project,
        DevWorkflowGraphNode node,
        DevWorkflowRunSnapshot run,
        DevWorkflowNodeRunSnapshot nodeRun,
        DevelopmentRepositoryBinding repository) =>
        new()
        {
            ProjectId = project.Id,
            TaskId = WorkspaceIdentity(run, nodeRun),
            AttemptId = WorkspaceIdentity(run, nodeRun),
            SelectedFolderId = repository.SelectedFolderId,
            RepositoryIdentityHash = project.RepositoryIdentityHash,
            BaseBranch = project.BaseBranch,
            EgressPolicy = project.EgressPolicy,
            ConfigurationVersion = project.ConfigurationVersion,
            TrustedRepositoryAcknowledged = project.TrustedRepositoryAcknowledged,
            TrustedRepositoryPolicyVersion = project.TrustedRepositoryPolicyVersion,
            TrustedRepositoryAcknowledgedAtUtc = project.TrustedRepositoryAcknowledgedAtUtc,
            MaxTokens = project.MaxTokens,
            MaxDurationSeconds = project.MaxDurationSeconds,
            Title = node.Label,
            Requirements = node.Instructions ?? $"Run the '{node.Label}' validation commands.",
            AcceptanceCriteriaJson = "[]",
            TaskStatus = DevelopmentTaskStatus.InProgress,
            TaskVersion = 1,
            AttemptRole = DevelopmentAttemptRole.Coder,
            AttemptStatus = PersistenceDevelopmentAttemptStatus.Running,
            ModelId = ExecutorIdentity,
            Provider = ExecutorIdentity,
            AttemptVersion = 1,
            CommandProfileJson = project.CommandProfileJson
        };

    /// <summary>The workspace this ATTEMPT owns. See <see cref="Synthesize" /> for why it is not the node run's id.</summary>
    internal static Guid WorkspaceIdentity(DevWorkflowRunSnapshot run, DevWorkflowNodeRunSnapshot nodeRun)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(nodeRun);
        return DevWorkflowOperationId.For(run.Id, nodeRun.NodeKey, nodeRun.Attempt, "workspace");
    }

    private static DevWorkflowToolRun Refused(string failureClass, string sanitizedReason, CollectingWorkspaceSecretsSink secrets) =>
        new()
        {
            Passed = false,
            FailureClass = failureClass,
            FailureCode = null,
            SanitizedReason = sanitizedReason,
            CommandsRun = 0,
            CommandsFailed = 0,
            TestsPassed = null,
            TestsFailed = null,
            Report = ReadOnlyMemory<byte>.Empty,
            SecretPaths = secrets.Paths
        };

    /// <summary>
    ///     The workflow's credential sink: it collects, and the dispatcher's tick records. Nothing detached writes.
    /// </summary>
    private sealed class CollectingWorkspaceSecretsSink : IDevelopmentWorkspaceSecretsSink
    {
        public IReadOnlyList<string> Paths { get; private set; } = [];

        public Task RecordAsync(Guid isolationKey, Guid attemptKey, IReadOnlyList<string> repositoryRelativePaths, CancellationToken cancellationToken = default)
        {
            Paths = repositoryRelativePaths ?? [];
            return Task.CompletedTask;
        }
    }
}

/// <summary>The report a Tool node-run leaves: what ran, against which commit and profile, and the gate's verdict.</summary>
/// <remarks>
///     Deliberately NOT <c>DevelopmentValidationReport</c>: that record's subject, manifest and expected-result
///     hashes describe a coder attempt's patch, and filling three hash fields with placeholders would be a report
///     claiming evidence it does not have.
/// </remarks>
internal sealed record DevWorkflowValidationReport(
    bool Passed,
    string NodeKey,
    int Attempt,
    string BaseCommit,
    string CommandProfileId,
    string CommandProfileDigest,
    string? FailureCode,
    string? FailureDetail,
    IReadOnlyList<DevelopmentCommandEvidence> Commands,
    long CompletedAtUtc,
    DevWorkflowValidationBasedOn? BasedOn = null);

/// <summary>
///     What <see cref="DevWorkflowToolCommands.OverlayAsync" /> did: what the report should say it judged, or the
///     sanitized sentence that refuses the node because the child's work could not be put in front of it honestly.
/// </summary>
internal readonly record struct DevWorkflowOverlay(DevWorkflowValidationBasedOn? BasedOn, string? Refusal);

/// <summary>What the commands ran against when the base commit alone would not say it.</summary>
/// <remarks>
///     The upstream implementation task whose approved patch was overlaid onto the workspace first. Absent means
///     nothing was overlaid: either this node has no upstream implementation to judge, or the pass was refused.
/// </remarks>
internal sealed record DevWorkflowValidationBasedOn(Guid DevelopmentTaskId, string PatchHash, string Detail);
