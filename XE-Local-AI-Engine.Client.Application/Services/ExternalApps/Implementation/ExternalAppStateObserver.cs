namespace XE_Local_AI_Engine.Client.Services.ExternalApps.Implementation;

using System.Globalization;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Containers;

/// <summary>
///     Notices that a container the engine believes is running has stopped, and says so.
///     <para>
///         It exists because reconciliation runs at boot and on an explicit refresh only, and every instance read is
///         served from the database — so without this a <c>docker stop</c>, an out-of-memory kill or a crash leaves
///         the interface reporting the application as running indefinitely. The detailed listing is what makes it
///         work: <c>docker stop</c> leaves the container LISTED, so a poll built on an id-only list would see no
///         change at all.
///     </para>
///     <para>
///         It never starts, creates or removes anything. That is what keeps it a cheap observer rather than a second,
///         unsupervised reconciler, and what makes it safe to run while the operation runner is working: an instance
///         with a live operation or a held gate is skipped, so a lifecycle operation finishing in the same instant is
///         never overwritten.
///     </para>
///     <para>
///         An <see cref="IHostedService" /> with its own loop rather than a <c>BackgroundService</c>, because the
///         base class's entry point carries a name the External Apps layer is not allowed to write: an architecture
///         test greps this directory's raw text for Development Mode's container members, and one of them shares that
///         name.
///     </para>
/// </summary>
internal sealed class ExternalAppStateObserver : IHostedService, IDisposable
{
    private readonly ExternalAppInstanceGate _gate;
    private readonly ILogger<ExternalAppStateObserver> _logger;
    private readonly ExternalAppsOptions _options;
    private readonly IExternalAppEventPublisher _publisher;
    private readonly ExternalAppOperationRunner _runner;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ExternalAppService _service;
    private readonly CancellationTokenSource _stopping = new();
    private readonly TimeProvider _timeProvider;
    private Task? _loop;

    public ExternalAppStateObserver(IServiceScopeFactory scopeFactory,
        ExternalAppService service,
        ExternalAppInstanceGate gate,
        ExternalAppOperationRunner runner,
        IExternalAppEventPublisher publisher,
        IOptions<ExternalAppsOptions> options,
        TimeProvider timeProvider,
        ILogger<ExternalAppStateObserver> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _options = options.Value;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void Dispose()
    {
        _stopping.Dispose();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            // The registration stays so the composition root has one shape whether or not the feature is on; the
            // behaviour is what the flag gates.
            return Task.CompletedTask;
        }

        // CancellationToken.None on purpose: the loop's lifetime is the host's, not this start call's.
        _loop = Task.Run(() => PollAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);

        if (_loop is not { } loop)
        {
            return;
        }

        try
        {
            await loop.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Either the loop's own cancellation or a shutdown deadline that ran out. Neither is worth failing
            // shutdown over, and the loop touches no container on its way out.
        }
    }

    /// <summary>
    ///     One poll, <c>internal</c> so a test drives it directly. Each tick opens its OWN scope: the observer is a
    ///     singleton and the store holds a scoped database context, so a scope held across ticks would be one context
    ///     living for the life of the node.
    /// </summary>
    internal async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IExternalAppInstanceStore>();

        // The rows come first, and a tick with nothing to watch never touches the daemon at all.
        var candidates = (await store.ListAsync(cancellationToken).ConfigureAwait(false))
                         .Where(static row => row is { DesiredState: ExternalAppDesiredState.Running, Status: ExternalAppInstanceStatus.Running })
                         .ToList();
        if (candidates.Count == 0)
        {
            return;
        }

        var resolver = scope.ServiceProvider.GetRequiredService<IContainerRuntimeResolver>();
        var resolution = await resolver.ResolveAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!resolution.Ready)
        {
            // "There is no container runtime right now" is not evidence that an application stopped, and writing it
            // to every row would be an unrecoverable verdict drawn from an absent observation.
            return;
        }

        await using var runtime = await resolver.CreateRuntimeAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        // ONE call for the whole tick. The state word arrives with the ids, which is the entire reason this can tell
        // a stopped container from a removed one.
        var listed = await runtime
                         .ListContainersDetailedAsync(new Dictionary<string, string>(StringComparer.Ordinal)
                         {
                             [ExternalAppLabels.Owner] = ExternalAppLabels.OwnerValue,
                             [ExternalAppLabels.Install] = _service.InstallId
                         },
                         cancellationToken)
                         .ConfigureAwait(false);

        foreach (var row in candidates)
        {
            try
            {
                await JudgeAsync(store, row, listed, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // One row, not the tick. A stored snapshot this engine can no longer read would otherwise cost every
                // later instance its observation for as long as that row exists, which is forever.
                _logger.LogWarning(exception, "Observing external application instance {InstanceId} failed; the rest of the tick continues.", row.Id);
            }
        }
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.ObserverIntervalSeconds), _timeProvider);

        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                await PollOnceAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // A tick that failed must not end the observer; the next one asks the daemon again.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                _logger.LogWarning(exception, "Observing external application containers failed; retrying on the next tick.");
            }
        }
    }

    private async Task JudgeAsync(IExternalAppInstanceStore store,
        ExternalAppInstanceSnapshot row,
        IReadOnlyList<ContainerSummary> listed,
        CancellationToken cancellationToken)
    {
        if (_runner.IsRunning(row.Id))
        {
            return;
        }

        using var lease = await _gate.TryEnterAsync(ExternalAppInstanceGate.InstanceKey(row.Id)).ConfigureAwait(false);
        if (lease is null)
        {
            // An operation owns this instance. Its own final write is the newer verdict, and overwriting it from an
            // observation taken before the gate was tried would undo it.
            return;
        }

        var instanceKey = row.Id.ToString("N", CultureInfo.InvariantCulture);
        var byService = new Dictionary<string, ContainerSummary>(StringComparer.Ordinal);
        foreach (var container in listed)
        {
            if (container.Labels.TryGetValue(ExternalAppLabels.Instance, out var instance)
                && string.Equals(instance, instanceKey, StringComparison.Ordinal)
                && container.Labels.TryGetValue(ExternalAppLabels.Service, out var serviceName))
            {
                byService[serviceName] = container;
            }
        }

        foreach (var serviceName in ExternalAppService.DeserializeManifest(row.ManifestSnapshotJson).Services.Select(static service => service.Name))
        {
            if (!byService.TryGetValue(serviceName, out var container))
            {
                await ReportStoppedAsync(store, row, $"The container for service '{serviceName}' is no longer on the container runtime.", cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (!string.Equals(container.State, "running", StringComparison.Ordinal))
            {
                var exitCode = container.ExitCode is { } code
                    ? string.Create(CultureInfo.InvariantCulture, $" with exit code {code}")
                    : string.Empty;
                await ReportStoppedAsync(store, row, $"Service '{serviceName}' is {container.State}{exitCode}.", cancellationToken).ConfigureAwait(false);
                return;
            }
        }
    }

    private async Task ReportStoppedAsync(IExternalAppInstanceStore store,
        ExternalAppInstanceSnapshot row,
        string summary,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var result = await store.UpdateStatusAsync(new ExternalAppStatusUpdate(row.Id,
                                    row.Version,
                                    new HashSet<ExternalAppInstanceStatus> { row.Status },
                                    ExternalAppInstanceStatus.StoppedUnexpectedly,
                                    ExternalAppInstanceEventKind.StoppedUnexpectedly,
                                    EventDetailJson: null,
                                    now,
                                    StoppedAtUtc: now,
                                    FailureCategory: ExternalAppFailureCategory.StoppedUnexpectedly,
                                    FailureSummary: summary),
                                cancellationToken)
                                .ConfigureAwait(false);

        if (!result.Applied)
        {
            // The row moved between the listing and this write. The other writer saw the instance more recently than
            // this tick did, so its verdict stands and the next tick re-reads.
            return;
        }

        _logger.LogWarning("External application instance {InstanceId} stopped without being asked to: {Summary}", row.Id, summary);

        try
        {
            await _publisher.PublishAsync(row.Id, result.Sequence, ExternalAppInstanceEventKind.StoppedUnexpectedly, ExternalAppInstanceStatus.StoppedUnexpectedly,
                                CancellationToken.None)
                            .ConfigureAwait(false);
        }
#pragma warning disable CA1031 // A subscriber that cannot be reached must not fail the observation it describes.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            _logger.LogDebug(exception, "Publishing the stopped-unexpectedly event for external application instance {InstanceId} failed.", row.Id);
        }
    }
}
