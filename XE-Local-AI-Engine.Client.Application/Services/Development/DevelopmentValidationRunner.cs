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
    IReadOnlyList<DevelopmentCommandEvidence> Commands,
    long CompletedAtUtc);

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
    internal const string ProfileVersion = "development-validation-v1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IDevelopmentEvidenceService _evidence;
    private readonly DevelopmentOptions _options;
    private readonly ISandboxRuntimeProvider _sandbox;
    private readonly IDevelopmentStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly IDevelopmentWorkspaceProvider _workspaceProvider;

    public DevelopmentValidationRunner(IDevelopmentStore store,
        IDevelopmentWorkspaceProvider workspaceProvider,
        ISandboxRuntimeProvider sandbox,
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
            var passed = commands.Length == profile.ValidationCommandIds.Count
                         && commands.All(static command => command.Completed && command.ExitCode == 0);
            var profileDigest = profile.ComputeDigest();
            var report = new DevelopmentValidationReport(passed,
                evidence.Current.BaseCommit,
                evidence.Current.SubjectHash,
                evidence.Current.ManifestHash,
                evidence.Current.ExpectedResultHash,
                ProfileVersion,
                profile.ProfileId,
                profileDigest,
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
                                    passed ? null : "Deterministic validation failed."),
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
}
