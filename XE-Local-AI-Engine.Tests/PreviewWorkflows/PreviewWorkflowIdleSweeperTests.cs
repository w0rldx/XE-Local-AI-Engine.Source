namespace XE_Local_AI_Engine.Tests.PreviewWorkflows;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.PreviewWorkflows;
using XE_Local_AI_Engine.Client.Services.PreviewWorkflows;
using XE_Local_AI_Engine.Client.Services.PreviewWorkflows.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Unit tests for the hosted service that drives the preview-run sweep cadence. The sweep logic itself is covered by
///     <see cref="PreviewWorkflowExecutionServiceTests" />; what is asserted here is the part only the hosted service
///     owns — that a tick actually reaches <c>SweepAsync</c>, that a non-positive configured interval falls back instead
///     of throwing out of <c>PeriodicTimer</c> (which would silently take the sweeper down and leak run slots forever),
///     and that cancellation stops the loop rather than faulting the host.
/// </summary>
public sealed class PreviewWorkflowIdleSweeperTests
{
    [Test]
    public async Task ExecuteAsync_OnEachTick_RunsTheSweep()
    {
        var time = new ManualTimeProvider();
        await using var service = CreateExecutionService(time, SweepInterval(TimeSpan.FromSeconds(30)));
        var runId = await StartAndCompleteRunAsync(service);
        using var sweeper = CreateSweeper(service, time, SweepInterval(TimeSpan.FromSeconds(30)));
        using var cancellation = new CancellationTokenSource();

        await RunLoopAsync(sweeper, cancellation, async () =>
        {
            // Past the replay retention, so the sweep the tick triggers has observable work to do.
            time.Advance(TimeSpan.FromSeconds(121));

            await AssertEx.EventuallyAsync(() => service.SnapshotBufferedEvents(runId, afterSeq: -1).Count == 0,
                TimeSpan.FromSeconds(5),
                "A sweeper tick must reach SweepAsync — otherwise terminal run logs are retained forever.");
        });
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    public async Task ExecuteAsync_WhenTheConfiguredIntervalIsNotPositive_FallsBackToThirtySeconds(int intervalSeconds)
    {
        // PeriodicTimer rejects a non-positive period, so without the fallback the sweeper would die on its first line
        // and every abandoned run would hold its concurrency slot until a node restart.
        var time = new ManualTimeProvider();
        var options = SweepInterval(TimeSpan.FromSeconds(intervalSeconds));
        await using var service = CreateExecutionService(time, options);
        var runId = await StartAndCompleteRunAsync(service);
        using var sweeper = CreateSweeper(service, time, options);
        using var cancellation = new CancellationTokenSource();

        await RunLoopAsync(sweeper, cancellation, async () =>
        {
            time.Advance(TimeSpan.FromSeconds(29));
            AssertEx.NotEmpty(service.SnapshotBufferedEvents(runId, afterSeq: -1));

            time.Advance(TimeSpan.FromSeconds(92));
            await AssertEx.EventuallyAsync(() => service.SnapshotBufferedEvents(runId, afterSeq: -1).Count == 0,
                TimeSpan.FromSeconds(5),
                "The fallback interval must still tick rather than leaving the sweeper inert.");
        });
    }

    [Test]
    public async Task ExecuteAsync_WhenCancelled_StopsWithoutFaultingTheHost()
    {
        var time = new ManualTimeProvider();
        var options = SweepInterval(TimeSpan.FromSeconds(30));
        await using var service = CreateExecutionService(time, options);
        using var sweeper = CreateSweeper(service, time, options);
        using var cancellation = new CancellationTokenSource();

        await RunLoopAsync(sweeper, cancellation, static () => Task.CompletedTask);
    }

    /// <summary>
    ///     Runs the sweeper loop for the duration of <paramref name="body" /> and always drains it before the caller's
    ///     <c>using</c> scope disposes the sweeper or the token source, including when the body throws.
    /// </summary>
    private static async Task RunLoopAsync(PreviewWorkflowIdleSweeper sweeper, CancellationTokenSource cancellation, Func<Task> body)
    {
        var loop = BackgroundServiceTestHelper.RunExecuteAsync(sweeper, cancellation.Token);
        try
        {
            await body();
        }
        finally
        {
            await cancellation.CancelAsync();
            await loop;
        }
    }

    private static PreviewWorkflowExecutionOptions SweepInterval(TimeSpan interval) =>
        new()
        {
            IdleTimeout = TimeSpan.FromMinutes(5),
            MaxRunDuration = TimeSpan.FromMinutes(15),
            SweepInterval = interval,
            AbandonedSubscriberGrace = TimeSpan.FromMinutes(5),
            MaxConcurrentRuns = 4,
            MaxOutputBytes = 10 * 1024 * 1024,
            ReplayRetention = TimeSpan.FromSeconds(60)
        };

    private static PreviewWorkflowExecutionService CreateExecutionService(TimeProvider time, PreviewWorkflowExecutionOptions options)
    {
        var runner = new FakePreviewWorkflowRunner((_, _) =>
            new ScriptedPreviewRunSession([PreviewWorkflowUpdate.RunCompleted("done")]));
        var resolver = SingleProviderResolverFactory.Create(new FakeLocalModelProvider(), maxLoadedProcesses: 8);
        return new PreviewWorkflowExecutionService(resolver,
            runner,
            new RecordingPreviewEventPublisher(),
            Options.Create(options),
            time,
            NullLoggerFactory.Instance);
    }

    private static PreviewWorkflowIdleSweeper CreateSweeper(PreviewWorkflowExecutionService service,
        TimeProvider time,
        PreviewWorkflowExecutionOptions options) =>
        new(service, Options.Create(options), time, new RecordingLogger<PreviewWorkflowIdleSweeper>());

    private static async Task<Guid> StartAndCompleteRunAsync(PreviewWorkflowExecutionService service)
    {
        var runId = await service.StartAsync(PreviewGraphBuilder.Linear(), connectionId: null);
        await AssertEx.EventuallyAsync(() => service.ActiveRunIds.Count == 0, TimeSpan.FromSeconds(5));
        AssertEx.NotEmpty(service.SnapshotBufferedEvents(runId, afterSeq: -1));
        return runId;
    }
}
