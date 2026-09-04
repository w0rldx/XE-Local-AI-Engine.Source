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
    /// <summary>
    ///     Bumped from <c>development-validation-v1</c> when the gate gained structured test results: a v1 report was
    ///     produced by a gate that checked exit codes only, so it cannot be treated as evidence for the rule the apply
    ///     and reviewer gates now enforce. Bumping the protocol version makes an old artifact fail the version check
    ///     explicitly, rather than fail the new count rule accidentally because its absent counts deserialize to zero.
    /// </summary>
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
        var task = await _store.GetTaskAsync(taskId, cancellationToken).ConfigureAwait(false);
        var transition = await _store.StartValidationAsync(new DevelopmentStartValidationCommand(taskId,
                                             Guid.NewGuid(),
                                             task.Version),
                                         cancellationToken)
                                     .ConfigureAwait(false);
        var coderAttempt = (await _store.ListAttemptsAsync(taskId, cancellationToken).ConfigureAwait(false))
            .Last(attempt => attempt.Role == DevelopmentAttemptRole.Coder
                             && attempt.Status == DevelopmentAttemptStatus.Succeeded);

        try
        {
            var snapshot = await _store.GetExecutionSnapshotAsync(coderAttempt.Id, cancellationToken).ConfigureAwait(false);
            var profile = DevelopmentCommandProfileCatalog.ResolveStored(snapshot.CommandProfileJson);
            var session = await _workspaceProvider.PrepareAsync(snapshot, repository, cancellationToken).ConfigureAwait(false);
            var evidence = await _evidence.ResolveCurrentAsync(taskId, session, cancellationToken).ConfigureAwait(false);

            // BEFORE the command loop, and before the tools that would run it exist. A dependency-manifest change
            // cannot be resolved by an attempt whose sandbox has no egress, so running restore/build/test to watch
            // them fail would spend the whole attempt budget arriving at a less specific answer than the one already
            // known. Zero command evidence is the honest report of a gate that deliberately ran nothing.
            var verdict = DevelopmentDependencyManifestPolicy.Evaluate(evidence.Current);
            IReadOnlyList<DevelopmentCommandEvidence> commands = [];
            if (verdict is null)
            {
                var tools = new DevelopmentWorkspaceTools(_sandbox, session, Options.Create(_options), profile);

                // The validation run had no overall deadline of its own: it was bounded only by each command's timeout,
                // which before per-command budgets existed was the whole attempt cap per command. A four-command profile
                // could therefore run for four times the cap it was supposed to respect.
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(Math.Min(snapshot.MaxDurationSeconds ?? _options.MaxAttemptDurationSeconds,
                    _options.MaxAttemptDurationSeconds)));

                foreach (var commandId in profile.ValidationCommandIds)
                {
                    _ = await tools.RunCommandAsync(commandId, timeout.Token).ConfigureAwait(false);
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
                cancellationToken).ConfigureAwait(false);

            var target = TargetFor(passed);
            _ = await _store.FinalizeValidationAsync(new DevelopmentFinalizeValidationCommand(prepared.Attachment,
                                    Guid.NewGuid(),
                                    transition.Version,
                                    target,
                                    passed ? null : BuildFailureReason(verdict)),
                                cancellationToken)
                            .ConfigureAwait(false);
            return new DevelopmentValidationResult(prepared.ArtifactId, passed, target, evidence.Current.SubjectHash);
        }
        catch (Exception exception)
        {
            try
            {
                // ONE automatic re-run, then a human. The recovery hop puts the task back at InProgress behind a
                // SUCCEEDED coder attempt — the state the next-action decision reads as "implemented, validate it" —
                // so nothing but this count stops a validation that throws deterministically from re-running for as
                // long as anything keeps asking. Two things remove every other brake: the Validation branch of
                // StartNextActionAsync writes no operation-ledger row of its own, and a workflow tick re-derives the
                // SAME operation id each time because neither the status, the round nor the attempt count has moved.
                //
                // The ledger IS the counter, and it costs nothing extra: the recovery transition is written under an
                // id derived from the coder attempt it recovers, so "has this attempt already been recovered?" is one
                // keyed read rather than a scan of the project's whole event log — no second write, no new store
                // method, no migration. A NEW coder attempt derives a NEW id and counts from zero, which is right: a
                // different implementation has not been tried yet.
                var recovery = RecoveryOperationId(coderAttempt.Id);
                var alreadyRecovered = await _store.FindOperationAsync(task.ProjectId,
                                                       recovery,
                                                       DevelopmentOperationPhases.Completed,
                                                       CancellationToken.None)
                                                   .ConfigureAwait(false) is not null;
                _ = await _store.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(taskId,
                                        alreadyRecovered ? Guid.NewGuid() : recovery,
                                        alreadyRecovered ? DevelopmentTaskStatus.Blocked : DevelopmentTaskStatus.InProgress,
                                        transition.Version,
                                        alreadyRecovered
                                            ? BuildRecoveryExhaustedReason(exception)
                                            : "Deterministic validation did not produce usable evidence."),
                                    CancellationToken.None)
                                .ConfigureAwait(false);
            }
            catch (Exception recoveryException) when (recoveryException is DevelopmentConcurrencyException or DevelopmentInvalidTransitionException)
            {
                // A concurrent operator transition already determined the authoritative task state. Nothing is banked
                // either, so the next exception is treated as the first — which is correct: nothing recovered.
            }

            throw;
        }
    }

    /// <summary>
    ///     Where a finished deterministic gate leaves the task.
    ///     <para>
    ///         A FAILED gate hands the failure to the CODER as a change request. Returning the task to
    ///         <c>InProgress</c> put it back in the exact state that means "implemented, validate it" — a succeeded
    ///         coder attempt and no evidence of the round the gate has just judged — so
    ///         <c>DevelopmentManagementService.StartNextActionAsync</c> read it back and scheduled the same validation
    ///         again, and again. Measured live on 2026-09-04: 289 restore/build/test runs on one task in 25 minutes,
    ///         zero coder rounds, ended only by cancelling the run.
    ///     </para>
    ///     <para>
    ///         Named rather than inlined so the one expression that routes a gate verdict has one home, and so a test
    ///         driving the gate's persistence hops without a workspace routes through it rather than restating it.
    ///     </para>
    /// </summary>
    internal static DevelopmentTaskStatus TargetFor(bool passed) =>
        passed ? DevelopmentTaskStatus.InReview : DevelopmentTaskStatus.ChangesRequested;

    /// <summary>
    ///     The task's terminal reason. It is clamped because <c>development_tasks.terminal_reason</c> is
    ///     <c>HasMaxLength(1024)</c> and the detail interpolates a parser message that a future adapter could make
    ///     arbitrarily long.
    /// </summary>
    private static string BuildFailureReason(DevelopmentValidationVerdict verdict) =>
        Clamp($"Deterministic validation failed ({verdict.FailureCode}): {verdict.FailureDetail}");

    /// <summary>
    ///     What an operator is told when the gate has now thrown twice on the same implementation.
    ///     <para>
    ///         The exception's TYPE, never its message. The message is the one string on this path that nothing has
    ///         sanitized — it can carry a host path or a fragment of a prompt — and the obvious sanitizer,
    ///         <c>DevelopmentArtifactSanitizer.SanitizeText</c>, REJECTS its input on a credential-like match rather
    ///         than redacting it, so calling it here would throw a second exception out of a catch block whose whole
    ///         job is to leave the task in a legible state. A type name is a code identifier: bounded, and incapable
    ///         of naming this machine. The full detail still reaches the engine log, because this method's caller
    ///         rethrows.
    ///     </para>
    /// </summary>
    private static string BuildRecoveryExhaustedReason(Exception exception) =>
        Clamp($"Deterministic validation failed twice on this implementation without producing usable evidence ({exception.GetType().Name}). The engine log has the detail.");

    /// <summary>
    ///     The operation id the recovery hop for one coder attempt is written under, and therefore the key that says
    ///     whether that attempt has already had its one free re-run. Derived rather than random so the SECOND recovery
    ///     of the same attempt can find the first with a single keyed read.
    /// </summary>
    private static Guid RecoveryOperationId(Guid coderAttemptId) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(coderAttemptId.ToString("N"), ":validation-recovery"))).AsSpan(0, 16));

    private static string Clamp(string reason) =>
        reason.Length <= MaxTerminalReasonLength ? reason : reason[..MaxTerminalReasonLength];

    private const int MaxTerminalReasonLength = 1024;
}
