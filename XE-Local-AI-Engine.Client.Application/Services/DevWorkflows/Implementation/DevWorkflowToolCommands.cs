namespace XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;

using System.Text.Json;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using PersistenceDevelopmentAttemptStatus = XE_Local_AI_Engine.Client.Persistence.Entities.DevelopmentAttemptStatus;

/// <summary>
///     Runs a Tool node's validation commands in a prepared sandbox workspace.
///     <para>
///         It calls the substrate BELOW <c>DevelopmentValidationRunner</c> rather than that runner, because the runner
///         is welded to the Dev Mode task machine: it starts a validation transition on a task row, reads that task's
///         last succeeded coder attempt, and finalizes by writing a task status. A workflow Tool node-run has none of
///         those rows. What it does share is everything that matters — the same workspace provider, the same command
///         profile, the same sanitizer and the same verdict — so the gate a workflow node applies is the gate Dev Mode
///         applies, not a second one that drifted.
///     </para>
///     <para>
///         The one thing that had to move is where committed credentials are reported. That write resolved the project
///         from a task row, so it is taken through <see cref="IDevelopmentWorkspaceSecretsSink" />, and this lane hands
///         the provider a sink that simply collects them for the tick to record.
///     </para>
/// </summary>
internal sealed class DevWorkflowToolCommands : IDevWorkflowToolCommands
{
    /// <summary>camelCase, matching every other document this product puts on a wire.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    ///     What the synthesized snapshot answers for the two attempt-identity fields. They exist for the cloud role
    ///     route, which a Tool node never reaches; naming them for what this is beats borrowing a model id that would
    ///     read as a claim about which model ran.
    /// </summary>
    private const string ExecutorIdentity = "dev-workflow-tool-node";

    /// <summary>What stands in for captured output the report had no room for. See <c>Compose</c>.</summary>
    private const string OutputElided = "(The captured output was too large for one artifact and was left out of this report.)";

    private readonly IDevelopmentRepositoryBindingService _bindings;
    private readonly IDevelopmentStore _development;
    private readonly DevelopmentOptions _developmentOptions;
    private readonly DevWorkflowOptions _options;
    private readonly IDevelopmentSandboxRuntimeProvider _sandbox;
    private readonly IServiceProvider _services;
    private readonly TimeProvider _timeProvider;

    public DevWorkflowToolCommands(IDevelopmentStore development,
        IDevelopmentRepositoryBindingService bindings,
        IDevelopmentSandboxRuntimeProvider sandbox,
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
            return await ExecuteAsync(run, node, nodeRun, secrets, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is DevelopmentRepositoryStateConflictException
                                              or SandboxCapabilityNotSupportedException
                                              or KeyNotFoundException)
        {
            // The node cannot run AS CONFIGURED: a project row that is gone, a repository that needs reconnecting, a
            // sandbox backend that cannot hold a trusted workspace. A human changes something or nothing changes.
            // Ahead of the security catch because the repository conflict IS one, and it is the more specific answer.
            return Refused(DevWorkflowFailureClasses.Configuration, Sanitized(exception), secrets);
        }
        catch (DevelopmentWorkspaceSecurityException exception)
        {
            // A protected path, an unacknowledged repository, a command that moved the worktree off its base commit, or
            // evidence carrying a credential no redaction can salvage. None of them is answered differently by running
            // the same commands again, so the class is the non-retryable one.
            return Refused(DevWorkflowFailureClasses.Policy, Sanitized(exception), secrets);
        }
    }

    /// <summary>
    ///     One exception's message, fit to be stored on a row and rendered on a wire.
    ///     <para>
    ///         These sentences are the ONE thing this lane surfaces that nothing has already redacted: the sandbox
    ///         interpolates an inner IOException's text into its own failure message, and that text can carry a host
    ///         path. The report artifact goes through the same sanitizer, so this closes the other half.
    ///     </para>
    ///     <para>
    ///         No protected roots are passed, deliberately: the generic absolute-path patterns fire on any path, and at
    ///         this point the repository may not have resolved. A message the sanitizer REFUSES — one carrying
    ///         credential-like material it cannot redact — is replaced wholesale rather than allowed to escape as a
    ///         second exception, because the failure being reported is the one worth surfacing.
    ///     </para>
    /// </summary>
    private static string Sanitized(Exception exception)
    {
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

        var project = await _development.GetProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
        var repository = await _bindings.ResolveProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
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
        // the Dev Mode store write, which resolves a task row that a node run does not have. Everything else it needs
        // comes from the container, so the wiring cannot drift from the registered one.
        var workspaces = ActivatorUtilities.CreateInstance<DevelopmentWorkspaceProvider>(_services, secrets);
        var snapshot = Synthesize(project, node, run, nodeRun, repository);
        var session = await workspaces.PrepareAsync(snapshot, repository, cancellationToken).ConfigureAwait(false);
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
                _ = await tools.RunCommandAsync(commandId, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The node's own budget, not the drain's — the drain's cancel propagates, because only the lane knows
            // whether a run was cancelled or paused.
            //
            // The commands that DID finish are still evidence, and the report says which of them passed before the
            // clock ran out: that is what tells an operator whether this node is slow or stuck, and it is the same
            // artifact every other outcome leaves, so the node run that times out is not the one nobody can read.
            return Result(timedOutAfterSeconds: budgetSeconds);
        }

        return Result(timedOutAfterSeconds: null);

        DevWorkflowToolRun Result(int? timedOutAfterSeconds)
        {
            var protectedRoots = DevelopmentArtifactSanitizer.ResolveProtectedRoots(repository.RepositoryRoot, session);
            var evidence = tools.CommandEvidence.Select(command => DevelopmentArtifactSanitizer.Sanitize(command, protectedRoots)).ToArray();

            // Evaluated against the list this node actually ran. The verdict's first rule is that every declared command
            // produced evidence, and a node narrowing the profile's list would otherwise fail that rule by construction.
            // A timed-out pass fails that same rule for a real reason — the commands it never reached — so the report
            // names the missing evidence and the row names the clock.
            var verdict = DevelopmentValidationVerdict.Evaluate(profile with
            {
                ValidationCommandIds = commandIds
            }, evidence);

            var tests = evidence.Select(static command => command.TestOutcome).OfType<DevelopmentTestOutcome>().Where(static outcome => outcome.Parsed).ToList();
            var passed = verdict.Passed && timedOutAfterSeconds is null;
            var refusal = timedOutAfterSeconds is null ? DevWorkflowFailureClasses.ToolCommandFailed : DevWorkflowFailureClasses.Timeout;
            var failureClass = passed ? null : refusal;

            return new DevWorkflowToolRun(passed,
                failureClass,
                verdict.FailureCode,
                timedOutAfterSeconds is { } seconds
                    ? $"This node run did not finish its validation commands within the {seconds} seconds it was given."
                    : verdict.FailureDetail,
                evidence.Length,
                evidence.Count(static command => !command.Completed || command.ExitCode != 0),
                tests.Count == 0 ? null : tests.Sum(static outcome => outcome.Passed),
                tests.Count == 0 ? null : tests.Sum(static outcome => outcome.Failed),
                Compose(verdict, profile, session, nodeRun, evidence),
                secrets.Paths);
        }
    }

    /// <summary>
    ///     The report bytes, bounded so the artifact store can always take them.
    ///     <para>
    ///         Each command's captured output is already capped, but a five-command profile can carry ten of those caps
    ///         and the artifact limit is one number below that. A document that will not fit therefore keeps every
    ///         command's identity, exit code, duration and test result and gives up only the captured text, with a line
    ///         saying so — an evidence record that names what failed beats an artifact write that throws and leaves the
    ///         node run with no evidence at all.
    ///     </para>
    /// </summary>
    private byte[] Compose(DevelopmentValidationVerdict verdict,
        DevelopmentCommandProfile profile,
        DevelopmentWorkspaceSession session,
        DevWorkflowNodeRunSnapshot nodeRun,
        IReadOnlyList<DevelopmentCommandEvidence> evidence)
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
            _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
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

    /// <summary>
    ///     The node's budget, the project's and the hard attempt cap, whichever is smallest. The cap is the outer bound
    ///     it claims to be, so a node asking for more than it gets less.
    ///     <para>
    ///         This bounds the PASS; the row's own deadline (<see cref="DevWorkflowDeadline" />, the node's timeout from
    ///         the instant the row started) bounds the node run. The two cannot disagree about the node's number because
    ///         this takes the smaller of it and the sandbox's, and this one is counted from the earlier instant — so a
    ///         pass answers with its evidence before the dispatcher would have to end the row without any.
    ///     </para>
    /// </summary>
    private int BudgetSeconds(DevWorkflowGraphNode node, DevelopmentProjectSnapshot project) =>
        Math.Min(Math.Min(node.NodeTimeoutSeconds ?? int.MaxValue, project.MaxDurationSeconds ?? int.MaxValue), _developmentOptions.MaxAttemptDurationSeconds);

    /// <summary>
    ///     The execution snapshot a Tool node-run stands in for a Dev Mode attempt with.
    ///     <para>
    ///         <b>Both isolation keys are the ATTEMPT's, not the node run's.</b> The provider partitions its worktree by
    ///         the task id and reuses a preserved one whole — including the base commit recorded in its manifest — so a
    ///         node run whose identity is constant across attempts would have its second attempt re-validate the FIRST
    ///         attempt's commit in the first attempt's tree, and report green or red about a state nothing is in any
    ///         more. Deriving the id from the attempt is what makes a re-attempt a real second try: the directory does
    ///         not exist, so the base branch is resolved again and the workspace is built from it.
    ///     </para>
    ///     <para>
    ///         Deterministic, and the same derivation the tick's own idempotency keys use, so a poll replayed after a
    ///         crash prepares the workspace this attempt already has rather than a second one beside it.
    ///     </para>
    /// </summary>
    internal static DevelopmentExecutionSnapshot Synthesize(DevelopmentProjectSnapshot project,
        DevWorkflowGraphNode node,
        DevWorkflowRunSnapshot run,
        DevWorkflowNodeRunSnapshot nodeRun,
        DevelopmentRepositoryBinding repository) =>
        new(project.Id,
            WorkspaceIdentity(run, nodeRun),
            WorkspaceIdentity(run, nodeRun),
            repository.SelectedFolderId,
            project.RepositoryIdentityHash,
            project.BaseBranch,
            project.EgressPolicy,
            project.ConfigurationVersion,
            project.TrustedRepositoryAcknowledged,
            project.TrustedRepositoryPolicyVersion,
            project.TrustedRepositoryAcknowledgedAtUtc,
            project.MaxTokens,
            project.MaxDurationSeconds,
            node.Label,
            node.Instructions ?? $"Run the '{node.Label}' validation commands.",
            "[]",
            DevelopmentTaskStatus.InProgress,
            TaskVersion: 1,
            DevelopmentAttemptRole.Coder,
            PersistenceDevelopmentAttemptStatus.Running,
            ExecutorIdentity,
            ExecutorIdentity,
            AttemptVersion: 1,
            project.CommandProfileJson);

    /// <summary>The workspace this ATTEMPT owns. See <see cref="Synthesize" /> for why it is not the node run's id.</summary>
    internal static Guid WorkspaceIdentity(DevWorkflowRunSnapshot run, DevWorkflowNodeRunSnapshot nodeRun)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(nodeRun);
        return DevWorkflowOperationId.For(run.Id, nodeRun.NodeKey, nodeRun.Attempt, "workspace");
    }

    private static DevWorkflowToolRun Refused(string failureClass, string sanitizedReason, CollectingWorkspaceSecretsSink secrets) =>
        new(Passed: false,
            failureClass,
            FailureCode: null,
            sanitizedReason,
            CommandsRun: 0,
            CommandsFailed: 0,
            TestsPassed: null,
            TestsFailed: null,
            ReadOnlyMemory<byte>.Empty,
            secrets.Paths);

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

/// <summary>
///     The report a Tool node-run leaves behind: what ran, against which commit and profile, and what the deterministic
///     gate made of it.
///     <para>
///         Deliberately NOT <c>DevelopmentValidationReport</c>. That record's subject, manifest and expected-result
///         hashes describe a coder attempt's patch, and a Tool node validates a clean checkout of the base commit —
///         filling three hash fields with placeholders would be a report claiming evidence it does not have.
///     </para>
/// </summary>
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
    long CompletedAtUtc);
