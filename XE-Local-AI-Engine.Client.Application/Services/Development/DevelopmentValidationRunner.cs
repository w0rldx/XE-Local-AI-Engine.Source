namespace XE_Local_AI_Engine.Client.Services.Development;

using System.Text.Json;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     The persisted validation report.
///     <para>
///         <see cref="CommandProfileVersion" /> is the artifact protocol version and keeps its exact former meaning —
///         the apply and reviewer gates still compare it against
///         <see cref="DevelopmentValidationRunner.ProfileVersion" />. <see cref="CommandProfileId" /> and
///         <see cref="CommandProfileDigest" /> are an additional, independent dimension recording which commands the
///         gate actually ran. Adding them does not weaken the protocol check; replacing the protocol check with them
///         would have.
///     </para>
/// </summary>
internal sealed record DevelopmentValidationReport(
    bool Passed,
    string BaseCommit,
    string SubjectHash,
    string ManifestHash,
    string ExpectedResultHash,
    string CommandProfileVersion,
    string CommandProfileId,
    string CommandProfileDigest,

    /// <summary>A stable <see cref="DevelopmentValidationFailureCodes" /> value when the gate failed, else null.</summary>
    string? FailureCode,

    /// <summary>Operator-facing detail for <see cref="FailureCode" />, or null when the gate passed.</summary>
    string? FailureDetail,
    IReadOnlyList<DevelopmentCommandEvidence> Commands,
    long CompletedAtUtc);

/// <summary>Stable <see cref="DevelopmentValidationReport.FailureCode" /> values, so a UI can localize them.</summary>
internal static class DevelopmentValidationFailureCodes
{
    /// <summary>Fewer command results were recorded than the profile declares validation commands.</summary>
    public const string MissingCommandEvidence = "missing_command_evidence";

    /// <summary>A validation command did not finish — it timed out or could not be launched.</summary>
    public const string CommandDidNotComplete = "command_did_not_complete";

    /// <summary>A validation command finished with a non-zero exit code.</summary>
    public const string CommandFailed = "command_failed";

    /// <summary>A test command ran but its result could not be read. This fails the gate; it never passes by default.</summary>
    public const string TestResultsUnparsed = "test_results_unparsed";

    /// <summary>A test command reported a readable result in which nothing actually ran.</summary>
    public const string NoTestsExecuted = "no_tests_executed";

    /// <summary>A test command reported failing tests.</summary>
    public const string TestsFailed = "tests_failed";
}

/// <summary>
///     The deterministic gate's verdict over one attempt's command evidence.
///     <para>
///         Exit codes alone were the whole gate until now, and they are not sufficient. A test command can exit
///         non-zero for reasons that have nothing to do with tests, and — the case that matters — a suite reduced to
///         zero tests can exit zero. So the verdict adds two rules on top of the exit codes, taken from the structured
///         result a code-owned adapter read: <strong>executed &gt; 0</strong> and <strong>failed == 0</strong>. And a
///         result the adapter could not read is a failure, never a pass: an unreadable result is exactly the state an
///         agent optimizing for green would produce if unreadable meant "assume fine".
///     </para>
/// </summary>
internal sealed record DevelopmentValidationVerdict(bool Passed, string? FailureCode, string? FailureDetail)
{
    private static readonly DevelopmentValidationVerdict Success = new(true, null, null);

    public static DevelopmentValidationVerdict Evaluate(DevelopmentCommandProfile profile,
        IReadOnlyList<DevelopmentCommandEvidence> commands)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(commands);

        if (commands.Count != profile.ValidationCommandIds.Count)
        {
            return new DevelopmentValidationVerdict(false,
                DevelopmentValidationFailureCodes.MissingCommandEvidence,
                $"The profile declares {profile.ValidationCommandIds.Count} validation commands but {commands.Count} produced evidence.");
        }

        foreach (var command in commands)
        {
            var failure = EvaluateCommand(command);
            if (failure is not null)
            {
                return failure;
            }
        }

        return Success;
    }

    /// <summary>
    ///     The per-command rules, in the order that yields the most useful reason rather than the earliest one. A
    ///     failing test makes its command exit non-zero too, so checking the exit code first would report every red
    ///     suite as the generic "command failed" and throw away the specific answer the adapter just produced.
    /// </summary>
    private static DevelopmentValidationVerdict? EvaluateCommand(DevelopmentCommandEvidence command)
    {
        if (!command.Completed)
        {
            return new DevelopmentValidationVerdict(false,
                DevelopmentValidationFailureCodes.CommandDidNotComplete,
                $"Command {command.CommandId} did not finish.");
        }

        if (command.TestOutcome is { } outcome)
        {
            if (!outcome.Parsed)
            {
                return new DevelopmentValidationVerdict(false,
                    DevelopmentValidationFailureCodes.TestResultsUnparsed,
                    $"Command {command.CommandId} produced no readable test result ({outcome.ParseFailureCode}): {outcome.ParseFailureDetail}");
            }

            if (outcome.Failed > 0)
            {
                return new DevelopmentValidationVerdict(false,
                    DevelopmentValidationFailureCodes.TestsFailed,
                    $"Command {command.CommandId} reported {outcome.Failed} failing of {outcome.Executed} executed tests.");
            }

            if (outcome.Executed == 0)
            {
                return new DevelopmentValidationVerdict(false,
                    DevelopmentValidationFailureCodes.NoTestsExecuted,
                    $"Command {command.CommandId} executed no tests ({outcome.Discovered} discovered). A change cannot be validated by a suite that ran nothing.");
            }
        }

        return command.ExitCode == 0
            ? null
            : new DevelopmentValidationVerdict(false,
                DevelopmentValidationFailureCodes.CommandFailed,
                $"Command {command.CommandId} exited with code {command.ExitCode}.");
    }
}

internal sealed record DevelopmentValidationResult(
    Guid ArtifactId,
    bool Passed,
    DevelopmentTaskStatus TaskStatus,
    string SubjectHash);

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

            var commands = tools.CommandEvidence
                                .Select(command => DevelopmentArtifactSanitizer.Sanitize(command,
                                    repository.RepositoryRoot,
                                    session.HostWorktreePath,
                                    session.RuntimePath))
                                .ToArray();
            var verdict = DevelopmentValidationVerdict.Evaluate(profile, commands);
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
