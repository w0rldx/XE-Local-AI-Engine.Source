namespace XE_Local_AI_Engine.Client.Services.Development;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Sandbox;

internal interface IDevelopmentValidationRunner
{
    Task<DevelopmentValidationResult> RunAsync(Guid taskId,
        DevelopmentRepositoryBinding repository,
        CancellationToken cancellationToken = default);
}

internal sealed class DevelopmentValidationRunner : IDevelopmentValidationRunner
{
    /// <summary>The artifact protocol version of a validation report.</summary>
    /// <remarks>
    ///     A <c>development-validation-v1</c> report came from a gate that checked exit codes only, so it is not
    ///     evidence for the rule the apply and reviewer gates now enforce; the version bump makes such an artifact
    ///     fail the version check explicitly, rather than fail the count rule because absent counts deserialize to 0.
    /// </remarks>
    internal const string ProfileVersion = "development-validation-v2";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IDevelopmentEvidenceService _evidence;
    private readonly DevelopmentOptions _options;
    private readonly IDevelopmentSandboxRuntimeProvider _sandbox;
    private readonly IDevelopmentStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly IDevelopmentWorkspaceProvider _workspaceProvider;

    public DevelopmentValidationRunner(IDevelopmentStore store,
        IDevelopmentWorkspaceProvider workspaceProvider,
        IDevelopmentSandboxRuntimeProvider sandbox,
        IDevelopmentEvidenceService evidence,
        IOptions<DevelopmentOptions> options,
        TimeProvider timeProvider)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _workspaceProvider = workspaceProvider ?? throw new ArgumentNullException(nameof(workspaceProvider));
        _sandbox = sandbox ?? throw new ArgumentNullException(nameof(sandbox));
        _evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<DevelopmentValidationResult> RunAsync(Guid taskId,
        DevelopmentRepositoryBinding repository,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        var task = await _store.GetTaskAsync(taskId, cancellationToken);
        var transition = await _store.StartValidationAsync(new DevelopmentStartValidationCommand
        {
            TaskId = taskId,
            OperationId = Guid.NewGuid(),
            ExpectedTaskVersion = task.Version
        },
                                         cancellationToken);
        var coderAttempt = (await _store.ListAttemptsAsync(taskId, cancellationToken))
            .Last(attempt => attempt.Role == DevelopmentAttemptRole.Coder
                             && attempt.Status == DevelopmentAttemptStatus.Succeeded);

        try
        {
            var snapshot = await _store.GetExecutionSnapshotAsync(coderAttempt.Id, cancellationToken);
            var profile = DevelopmentCommandProfileCatalog.ResolveStored(snapshot.CommandProfileJson);
            var session = await _workspaceProvider.PrepareAsync(snapshot, repository, cancellationToken);
            var evidence = await _evidence.ResolveCurrentAsync(taskId, session, cancellationToken);

            // Before the command loop and the tools that would run it: an egress-denied attempt cannot resolve a
            // changed manifest, so running the commands spends the budget on a vaguer answer. Zero evidence is honest.
            var verdict = DevelopmentDependencyManifestPolicy.Evaluate(evidence.Current);
            IReadOnlyList<DevelopmentCommandEvidence> commands = [];
            if (verdict is null)
            {
                var tools = new DevelopmentWorkspaceTools(_sandbox, session, Options.Create(_options), profile);

                // The validation run needs a deadline of its own: bounded only per command, a four-command profile runs
                // for four times the attempt cap it is meant to respect.
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(Math.Min(snapshot.MaxDurationSeconds ?? _options.MaxAttemptDurationSeconds,
                    _options.MaxAttemptDurationSeconds)));

                foreach (var commandId in profile.ValidationCommandIds)
                {
                    _ = await tools.RunCommandAsync(commandId, timeout.Token);
                }

                var protectedRoots = DevelopmentArtifactSanitizer.ResolveProtectedRoots(repository.RepositoryRoot, session);
                commands = tools.CommandEvidence
                                .Select(command => DevelopmentArtifactSanitizer.Sanitize(command, protectedRoots))
                                .ToArray();
                verdict = DevelopmentValidationVerdict.Evaluate(profile, commands);
            }

            var passed = verdict.Passed;
            var profileDigest = profile.ComputeDigest();
            var report = new DevelopmentValidationReport(passed,
                evidence.Current.BaseCommit,
                evidence.Current.SubjectHash,
                evidence.Current.ManifestHash,
                evidence.Current.ExpectedResultHash,
                ProfileVersion,
                profile.ProfileId,
                profileDigest,
                verdict.FailureCode,
                verdict.FailureDetail,
                commands,
                _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
            var prepared = await _evidence.PrepareAsync(snapshot,
                DevelopmentArtifactKind.ValidationReport,
                JsonSerializer.SerializeToUtf8Bytes(report, JsonOptions),
                evidence.Current,
                [evidence.PatchArtifact.Id, evidence.ManifestArtifact.Id],
                ProfileVersion,
                profileDigest,
                cancellationToken);

            var target = TargetFor(passed);
            _ = await _store.FinalizeValidationAsync(new DevelopmentFinalizeValidationCommand
            {
                Artifact = prepared.Attachment,
                OperationId = Guid.NewGuid(),
                ExpectedTaskVersion = transition.Version,
                TargetStatus = target,
                SanitizedReason = passed ? null : BuildFailureReason(verdict)
            },
                                cancellationToken);
            return new DevelopmentValidationResult { ArtifactId = prepared.ArtifactId, Passed = passed, TaskStatus = target, SubjectHash = evidence.Current.SubjectHash };
        }
        catch (Exception exception)
        {
            try
            {
                // One automatic re-run, then a human. Nothing else brakes it: the recovery hop leaves the task reading
                // as "implemented, validate it", and a workflow tick re-derives the same operation id every time.
                var recovery = RecoveryOperationId(coderAttempt.Id);
                var alreadyRecovered = await _store.FindOperationAsync(task.ProjectId,
                                                       recovery,
                                                       DevelopmentOperationPhases.Completed,
                                                       CancellationToken.None) is not null;
                _ = await _store.TransitionTaskAsync(new DevelopmentTransitionTaskCommand
                {
                    TaskId = taskId,
                    OperationId = alreadyRecovered ? Guid.NewGuid() : recovery,
                    TargetStatus = alreadyRecovered ? DevelopmentTaskStatus.Blocked : DevelopmentTaskStatus.InProgress,
                    ExpectedTaskVersion = transition.Version,
                    Reason = alreadyRecovered
                                            ? BuildRecoveryExhaustedReason(exception)
                                            : "Deterministic validation did not produce usable evidence."
                },
                                    CancellationToken.None);
            }
            catch (Exception recoveryException) when (recoveryException is DevelopmentConcurrencyException or DevelopmentInvalidTransitionException)
            {
                // A concurrent operator transition already determined the authoritative task state. Nothing is banked
                // either, so the next exception is treated as the first — which is correct: nothing recovered.
            }

            throw;
        }
    }

    /// <summary>Where a finished deterministic gate leaves the task.</summary>
    /// <remarks>
    ///     A failed gate hands the failure to the coder as a change request. Returning the task to <c>InProgress</c>
    ///     instead puts it back in the state meaning "implemented, validate it" — a succeeded coder attempt, no
    ///     evidence of the round just judged — so the next-action decision schedules the same validation again: 289
    ///     restore/build/test runs on one task in 25 minutes, zero coder rounds. Named so the one expression routing a
    ///     gate verdict has one home, and so a test can drive the persistence hops without a workspace.
    /// </remarks>
    internal static DevelopmentTaskStatus TargetFor(bool passed) =>
        passed ? DevelopmentTaskStatus.InReview : DevelopmentTaskStatus.ChangesRequested;

    /// <summary>The task's terminal reason.</summary>
    /// <remarks>
    ///     It is clamped because <c>development_tasks.terminal_reason</c> is <c>HasMaxLength(1024)</c> and the detail
    ///     interpolates a parser message a future adapter could make arbitrarily long.
    /// </remarks>
    private static string BuildFailureReason(DevelopmentValidationVerdict verdict) =>
        Clamp($"Deterministic validation failed ({verdict.FailureCode}): {verdict.FailureDetail}");

    /// <summary>What an operator is told when the gate has thrown twice on the same implementation.</summary>
    /// <remarks>
    ///     The exception's type, never its message: the message is the one string on this path nothing has sanitized
    ///     and can carry a host path or a prompt fragment, while <c>DevelopmentArtifactSanitizer.SanitizeText</c>
    ///     rejects its input on a credential-like match, so calling it would throw out of a catch whose job is to
    ///     leave the task legible. A type name is a bounded code identifier that cannot name this machine, and the
    ///     caller rethrows, so the full detail still reaches the engine log.
    /// </remarks>
    private static string BuildRecoveryExhaustedReason(Exception exception) =>
        Clamp($"Deterministic validation failed twice on this implementation without producing usable evidence ({exception.GetType().Name}). The engine log has the detail.");

    /// <summary>
    ///     The operation id the recovery hop for one coder attempt is written under, and so the key that says whether
    ///     that attempt has already had its one free re-run.
    /// </summary>
    /// <remarks>
    ///     The ledger is the counter, at no extra cost: derived rather than random, a second recovery of the same
    ///     attempt finds the first with one keyed read instead of a scan of the project's event log — no second
    ///     write, no new store method, no migration. A new coder attempt derives a new id and counts from zero, which
    ///     is right, because a different implementation has not been tried yet.
    /// </remarks>
    private static Guid RecoveryOperationId(Guid coderAttemptId) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(coderAttemptId.ToString("N"), ":validation-recovery"))).AsSpan(0, 16));

    private static string Clamp(string reason) =>
        reason.Length <= MaxTerminalReasonLength ? reason : reason[..MaxTerminalReasonLength];

    private const int MaxTerminalReasonLength = 1024;
}
