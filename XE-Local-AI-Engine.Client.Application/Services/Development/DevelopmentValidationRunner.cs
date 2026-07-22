namespace XE_Local_AI_Engine.Client.Services.Development;

using System.Text.Json;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Sandbox;

internal sealed record DevelopmentValidationReport(
    bool Passed,
    string BaseCommit,
    string SubjectHash,
    string ManifestHash,
    string ExpectedResultHash,
    string CommandProfileVersion,
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
        string repositoryRoot,
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
        string repositoryRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
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
            var session = await _workspaceProvider.PrepareAsync(snapshot, repositoryRoot, cancellationToken).ConfigureAwait(false);
            var evidence = await _evidence.ResolveCurrentAsync(taskId, session, cancellationToken).ConfigureAwait(false);
            var tools = new DevelopmentWorkspaceTools(_sandbox, session, Options.Create(_options));
            if (_options.ValidationCommandIds.Count == 0)
            {
                throw new InvalidOperationException("The deterministic Development validation command profile is empty.");
            }

            foreach (var commandId in _options.ValidationCommandIds)
            {
                _ = await tools.RunCommandAsync(commandId, cancellationToken).ConfigureAwait(false);
            }

            var commands = tools.CommandEvidence
                                .Select(command => DevelopmentArtifactSanitizer.Sanitize(command,
                                    repositoryRoot,
                                    session.HostWorktreePath,
                                    session.RuntimePath))
                                .ToArray();
            var passed = commands.Length == _options.ValidationCommandIds.Count
                         && commands.All(static command => command.Completed && command.ExitCode == 0);
            var report = new DevelopmentValidationReport(passed,
                evidence.Current.BaseCommit,
                evidence.Current.SubjectHash,
                evidence.Current.ManifestHash,
                evidence.Current.ExpectedResultHash,
                ProfileVersion,
                commands,
                _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
            var prepared = await _evidence.PrepareAsync(snapshot,
                DevelopmentArtifactKind.ValidationReport,
                JsonSerializer.SerializeToUtf8Bytes(report, JsonOptions),
                evidence.Current,
                [evidence.PatchArtifact.Id, evidence.ManifestArtifact.Id],
                ProfileVersion,
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
