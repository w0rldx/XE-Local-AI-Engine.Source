namespace XE_Local_AI_Engine.Client.Services.Development;

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

            var target = passed ? DevelopmentTaskStatus.InReview : DevelopmentTaskStatus.InProgress;
            _ = await _store.FinalizeValidationAsync(new DevelopmentFinalizeValidationCommand(prepared.Attachment,
                                    Guid.NewGuid(),
                                    transition.Version,
                                    target,
                                    passed ? null : BuildFailureReason(verdict)),
                                cancellationToken)
                            .ConfigureAwait(false);
            return new DevelopmentValidationResult(prepared.ArtifactId, passed, target, evidence.Current.SubjectHash);
        }
        catch
        {
            try
            {
                _ = await _store.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(taskId,
                                        Guid.NewGuid(),
                                        DevelopmentTaskStatus.InProgress,
                                        transition.Version,
                                        "Deterministic validation did not produce usable evidence."),
                                    CancellationToken.None)
                                .ConfigureAwait(false);
            }
            catch (Exception recoveryException) when (recoveryException is DevelopmentConcurrencyException or DevelopmentInvalidTransitionException)
            {
                // A concurrent operator transition already determined the authoritative task state.
            }

            throw;
        }
    }

    /// <summary>
    ///     The task's terminal reason. It is clamped because <c>development_tasks.terminal_reason</c> is
    ///     <c>HasMaxLength(1024)</c> and the detail interpolates a parser message that a future adapter could make
    ///     arbitrarily long.
    /// </summary>
    private static string BuildFailureReason(DevelopmentValidationVerdict verdict)
    {
        var reason = $"Deterministic validation failed ({verdict.FailureCode}): {verdict.FailureDetail}";
        return reason.Length <= MaxTerminalReasonLength ? reason : reason[..MaxTerminalReasonLength];
    }

    private const int MaxTerminalReasonLength = 1024;
}
