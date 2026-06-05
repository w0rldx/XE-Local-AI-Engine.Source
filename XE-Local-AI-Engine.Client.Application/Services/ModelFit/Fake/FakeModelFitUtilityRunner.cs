namespace XE_Local_AI_Engine.Client.Services.ModelFit.Fake;

using XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Deterministic, in-memory <see cref="IModelFitUtilityRunner" /> used as the default until the
///     <c>local-container</c> runner is selected, and as a CI double. It needs no HostAgent and no Docker: the run
///     result is scripted and the last request is recorded so a test can assert the intent translation. Mirrors the
///     production-resident, config-selected <c>FakeSandboxRuntimeProvider</c>.
/// </summary>
public sealed class FakeModelFitUtilityRunner : IModelFitUtilityRunner
{
    /// <summary>The runner name this fake registers under for configuration-bound selection.</summary>
    public const string Name = "fake";

    private readonly object _sync = new();

    private ModelFitUtilityRunResult _scriptedResult = new(Status: ModelFitRunStatus.Succeeded,
        ExitCode: 0,
        StandardOutput: "{}",
        StandardError: string.Empty,
        Completed: true,
        DurationMs: 0,
        StartedAtUtc: null,
        CompletedAtUtc: null,
        SanitizedError: null);

    private bool _throwCancellation;

    /// <summary>The last request passed to <see cref="RunAsync" />, or <c>null</c> when never called.</summary>
    public ModelFitUtilityRunRequest? LastRequest { get; private set; }

    /// <summary>How many times <see cref="RunAsync" /> was invoked.</summary>
    public int RunCount { get; private set; }

    public Task<ModelFitUtilityRunResult> RunAsync(ModelFitUtilityRunRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            LastRequest = request;
            RunCount++;
            if (_throwCancellation)
            {
                throw new OperationCanceledException("Scripted model-fit utility run cancellation.");
            }

            return Task.FromResult(_scriptedResult);
        }
    }

    /// <summary>Scripts the result the next run returns.</summary>
    public void ScriptResult(ModelFitUtilityRunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        lock (_sync)
        {
            _throwCancellation = false;
            _scriptedResult = result;
        }
    }

    /// <summary>
    ///     Scripts the next run to throw an <see cref="OperationCanceledException" /> (simulating a node-token cancel
    ///     mid-run), so callers can assert their cancellation-mapping path.
    /// </summary>
    public void ScriptThrowCancellation()
    {
        lock (_sync)
        {
            _throwCancellation = true;
        }
    }
}
