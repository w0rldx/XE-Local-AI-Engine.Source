namespace XE_Local_AI_Engine.Client.Services.ExternalApps.Implementation;

using System.Globalization;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Containers;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog;

/// <summary>
///     The boot pass that makes the stored instances agree with the daemon again, and the same pass the operator's
///     runtime refresh re-runs.
///     <para>
///         Three kinds of disagreement, each with exactly one verdict per row. A row in a TRANSIENT status is an
///         operation the host died inside: an interrupted uninstall is completed, everything else is settled to
///         <c>Failed</c> and never resumed — safe only because the instance's storage is kept, so the user's next
///         action is an ordinary reset or uninstall. A SETTLED row is judged on its desired state and what the daemon
///         actually holds, never on the status it was left with, so an instance whose containers survived a crash
///         comes back running. Anything the daemon holds under this installation that NO row claims is an orphan and
///         is removed with its network.
///     </para>
///     <para>
///         Two things it deliberately does not do. It never touches an instance a live operation holds — a refresh
///         during an update would remove containers under the updater, and a lost compare-and-swap cannot undo a
///         daemon mutation. And it writes nothing at all when no runtime is ready: rewriting every row to
///         <c>Failed</c> would destroy the evidence the next pass needs, and would leave an interrupted uninstall
///         unable ever to complete.
///     </para>
/// </summary>
internal sealed class ExternalAppStartupReconciler : IExternalAppStartupReconciler, IHostedService
{
    private const string UnverifiablePlanSummary =
        "The engine restarted and this application's stored configuration could not be verified against its containers; start it again to rebuild them.";

    private readonly ExternalAppInstanceGate _gate;
    private readonly ExternalAppStorageLayout _layout;
    private readonly ILogger<ExternalAppStartupReconciler> _logger;
    private readonly ExternalAppsOptions _options;
    private readonly IExternalAppEventPublisher _publisher;
    private readonly ExternalAppOperationRunner _runner;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ExternalAppService _service;
    private readonly TimeProvider _timeProvider;

    public ExternalAppStartupReconciler(IServiceScopeFactory scopeFactory,
        ExternalAppService service,
        ExternalAppStorageLayout layout,
        ExternalAppInstanceGate gate,
        ExternalAppOperationRunner runner,
        IExternalAppEventPublisher publisher,
        IOptions<ExternalAppsOptions> options,
        TimeProvider timeProvider,
        ILogger<ExternalAppStartupReconciler> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _options = options.Value;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    ///     One pass, and the whole body is guarded: a node whose Docker daemon is broken must still start, and an
    ///     application the user can see and act on is worth more than a reconciliation that ran.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        try
        {
            var summary = await ReconcileAsync(cancellationToken);
            if (summary != ExternalAppReconcileSummary.Nothing)
            {
                _logger.LogInformation("Reconciled external applications at startup: {Inspected} inspected, {Changed} changed, {Orphans} orphaned container(s) removed, {Busy} skipped as busy.",
                    summary.RowsInspected,
                    summary.RowsChanged,
                    summary.OrphansRemoved,
                    summary.RowsSkippedBusy);
            }
        }
#pragma warning disable CA1031 // Startup must not fail because a container daemon did; the next refresh re-runs this.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            _logger.LogWarning(exception, "Reconciling external applications at startup failed; the node is starting anyway.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // Nothing to stop, and nothing to do to the containers: installed applications keep serving while the engine
        // is down, which is the whole point of creating them with a restart policy the daemon owns.
        return Task.CompletedTask;
    }

    public async Task<ExternalAppReconcileSummary> ReconcileAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return ExternalAppReconcileSummary.Nothing;
        }

        // One scope for the whole pass: this is a singleton and the store holds a scoped database context, so there
        // is no request whose services could be borrowed.
        await using var scope = _scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IExternalAppInstanceStore>();
        var resolver = scope.ServiceProvider.GetRequiredService<IContainerRuntimeResolver>();

        var resolution = await resolver.ResolveAsync(cancellationToken: cancellationToken);
        if (!resolution.Ready)
        {
            _logger.LogInformation("No container runtime is ready ({Message}), so no external application row was judged.", resolution.Message);
            return ExternalAppReconcileSummary.Nothing;
        }

        await using var runtime = await resolver.CreateRuntimeAsync(cancellationToken: cancellationToken);
        return await ReconcileAsync(store, runtime, resolution.Daemon.IsRootless, cancellationToken);
    }

    private async Task<ExternalAppReconcileSummary> ReconcileAsync(IExternalAppInstanceStore store,
        IContainerRuntime runtime,
        bool daemonIsRootless,
        CancellationToken cancellationToken)
    {
        var rows = await store.ListAsync(cancellationToken);

        // ONE detailed list for every row: the state word and the exit code arrive with the ids, so a container that
        // is stopped but still listed is distinguishable from one that is gone without a second round trip per row.
        var owned = await runtime
                          .ListContainersDetailedAsync(new Dictionary<string, string>(StringComparer.Ordinal)
                              {
                                  [ExternalAppLabels.Owner] = ExternalAppLabels.OwnerValue,
                                  [ExternalAppLabels.Install] = _service.InstallId
                              },
                              cancellationToken);

        var byInstance = GroupByInstance(owned);
        var changed = 0;
        var skippedBusy = 0;
        var failed = 0;

        foreach (var row in rows)
        {
            _ = byInstance.TryGetValue(row.Id, out var containers);
            containers ??= [];

            RowVerdict verdict;
            try
            {
                verdict = await JudgeRowAsync(store, runtime, daemonIsRootless, row, containers, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // ONE row's judgement, not the pass. A container removed between the list and the inspect, or a
                // stored snapshot the policy refuses, would otherwise skip every later row AND the orphan sweep —
                // and reach the operator's refresh as a 500 rather than as an instance that kept its status.
                failed++;
                _logger.LogWarning(exception, "Judging external application instance {InstanceId} failed; it keeps its status and the pass continues.", row.Id);
                continue;
            }

            switch (verdict)
            {
                case RowVerdict.Busy:
                    skippedBusy++;
                    break;
                case RowVerdict.Changed:
                    changed++;
                    break;
                default:
                    break;
            }
        }

        var orphans = await RemoveOrphansAsync(store, runtime, byInstance, [.. rows.Select(static row => row.Id)], cancellationToken);
        var foreign = await CountForeignAsync(runtime, cancellationToken);

        return new ExternalAppReconcileSummary(rows.Count, changed, orphans, foreign, skippedBusy, failed);
    }

    /// <summary>
    ///     One row, under its own gate lease. The lease is taken and released per row rather than held across the
    ///     pass: holding it would answer every user command "an operation is already in flight" for as long as the
    ///     reconciliation runs.
    /// </summary>
    private async Task<RowVerdict> JudgeRowAsync(IExternalAppInstanceStore store,
        IContainerRuntime runtime,
        bool daemonIsRootless,
        ExternalAppInstanceSnapshot row,
        IReadOnlyList<ContainerSummary> containers,
        CancellationToken cancellationToken)
    {
        if (_runner.IsRunning(row.Id))
        {
            return RowVerdict.Busy;
        }

        // The gate is what every operation actually holds, so it — not the runner map — is the authority. A row a
        // live operation owns is skipped: removing its containers under it is a daemon mutation no lost
        // compare-and-swap could undo.
        using var lease = await _gate.TryEnterAsync(ExternalAppInstanceGate.InstanceKey(row.Id));
        if (lease is null)
        {
            return RowVerdict.Busy;
        }

        // The row in hand was read BEFORE the gate. An operation that finished and released the gate in between
        // leaves this pass judging a snapshot that is already history — and the verdicts here are daemon mutations,
        // which no lost compare-and-swap afterwards could undo: a stale 'Updating' would remove the containers the
        // update had just created. Everything below therefore judges the row as it is NOW, or nothing at all.
        var current = await store.GetAsync(row.Id, cancellationToken);
        if (current is null || current.Version != row.Version)
        {
            return RowVerdict.Busy;
        }

        return await JudgeAsync(store, runtime, daemonIsRootless, current, containers, cancellationToken)
            ? RowVerdict.Changed
            : RowVerdict.Unchanged;
    }

    /// <summary>Exactly one verdict per row: a transient status is an interrupted operation, anything else is judged on the daemon.</summary>
    private Task<bool> JudgeAsync(IExternalAppInstanceStore store,
        IContainerRuntime runtime,
        bool daemonIsRootless,
        ExternalAppInstanceSnapshot row,
        IReadOnlyList<ContainerSummary> containers,
        CancellationToken cancellationToken)
    {
        return row.Status switch
        {
            ExternalAppInstanceStatus.Uninstalling => CompleteUninstallAsync(store, runtime, row, cancellationToken),
            ExternalAppInstanceStatus.Installing
                or ExternalAppInstanceStatus.Starting
                or ExternalAppInstanceStatus.Stopping
                or ExternalAppInstanceStatus.Updating
                or ExternalAppInstanceStatus.Resetting => SettleInterruptedAsync(store, runtime, row, cancellationToken),
            _ => JudgeSettledAsync(store, runtime, daemonIsRootless, row, containers, cancellationToken)
        };
    }

    /// <summary>
    ///     Branch A, uninstall arm: finishes what the user already authorised, in the order the pipeline uses.
    ///     Resuming a destructive action somebody asked for is not a new destructive decision, and the rows go before
    ///     the directory so a directory that cannot be removed leaves a warning rather than an unreachable row.
    /// </summary>
    private async Task<bool> CompleteUninstallAsync(IExternalAppInstanceStore store,
        IContainerRuntime runtime,
        ExternalAppInstanceSnapshot row,
        CancellationToken cancellationToken)
    {
        if (!await _service.RemoveInstanceContainersAsync(runtime, row.Id, cancellationToken))
        {
            // The row stays in Uninstalling and the storage stays with it, which is what makes the NEXT pass run this
            // same branch again. Deleting the row now would leave a container the user asked to be gone running with
            // nothing left that describes it.
            _logger.LogWarning("The containers of external application instance {InstanceId} are still present, so its interrupted uninstall was not completed; the next pass retries it.",
                row.Id);

            return false;
        }

        try
        {
            // Same order as the pipeline's own uninstall: the data goes before the row, so a wipe this pass cannot
            // finish leaves the row in Uninstalling with its storage and the NEXT pass runs this branch again.
            await _service.WipeVolumeContentsAsync(runtime, row.Id, cancellationToken);
        }
        catch (ExternalAppPipelineException exception)
        {
            _logger.LogWarning(exception,
                "The data of external application instance {InstanceId} could not be removed, so its interrupted uninstall was not completed; the next pass retries it.",
                row.Id);

            return false;
        }

        if (!_service.DeleteStorage(row.Id))
        {
            _logger.LogWarning("External application instance {InstanceId} was uninstalled on boot but its data directory could not be removed; it can be deleted by hand.",
                row.Id);
        }

        if (!await store.DeleteAsync(row.Id, row.Version, cancellationToken))
        {
            _logger.LogWarning("Completing the interrupted uninstall of external application instance {InstanceId} lost its compare-and-swap; leaving it alone.",
                row.Id);
            return false;
        }

        // A synthetic sequence: the rows are gone, so no number can be minted for it. It is never replayable, which
        // is correct — there is nothing left to replay it from.
        await PublishAsync(row.Id, row.LastSequence + 1, ExternalAppInstanceEventKind.Uninstalled, ExternalAppInstanceStatus.Uninstalling);

        _gate.Forget(ExternalAppInstanceGate.InstanceKey(row.Id));
        return true;
    }

    /// <summary>
    ///     Branch A, everything else: settle, never resume. The containers and the network go, the STORAGE stays, and
    ///     the row keeps the snapshot and variables its insert wrote — so the user's next action is an ordinary reset
    ///     or uninstall rather than an instance nothing can act on.
    /// </summary>
    private async Task<bool> SettleInterruptedAsync(IExternalAppInstanceStore store,
        IContainerRuntime runtime,
        ExternalAppInstanceSnapshot row,
        CancellationToken cancellationToken)
    {
        // Best-effort: this branch destroys nothing and builds nothing. A container it could not remove keeps the row
        // Failed with its storage intact, which is the state the user's next reset or uninstall acts on anyway.
        _ = await _service.RemoveInstanceContainersAsync(runtime, row.Id, cancellationToken);

        return await WriteAsync(store,
                row,
                ExternalAppInstanceStatus.Failed,
                ExternalAppInstanceEventKind.Failed,
                ExternalAppFailureCategory.Unknown,
                $"The engine restarted while this application was {InterruptedVerb(row.Status)}.",
                cancellationToken);
    }

    private static string InterruptedVerb(ExternalAppInstanceStatus status)
    {
        return status switch
        {
            ExternalAppInstanceStatus.Installing => "being installed",
            ExternalAppInstanceStatus.Starting => "starting",
            ExternalAppInstanceStatus.Stopping => "stopping",
            ExternalAppInstanceStatus.Updating => "being updated",
            _ => "being reset"
        };
    }

    /// <summary>
    ///     Branch B. The verdict keys on the DESIRED state and on what the daemon holds, never on the stored status:
    ///     an instance the user wants running, whose containers survived the crash, is running whatever the row says.
    /// </summary>
    private async Task<bool> JudgeSettledAsync(IExternalAppInstanceStore store,
        IContainerRuntime runtime,
        bool daemonIsRootless,
        ExternalAppInstanceSnapshot row,
        IReadOnlyList<ContainerSummary> containers,
        CancellationToken cancellationToken)
    {
        var manifest = ExternalAppService.DeserializeManifest(row.ManifestSnapshotJson);
        var byService = ByService(containers);

        if (row.DesiredState == ExternalAppDesiredState.Stopped)
        {
            return await StopStrayContainersAsync(store, runtime, row, byService, cancellationToken);
        }

        // Missing or exited is decided from the LIST, before anything is inspected: it needs no plan, and it is the
        // case a snapshot that no longer plans must not be able to mask.
        foreach (var serviceName in manifest.Services.Select(static service => service.Name))
        {
            if (!byService.TryGetValue(serviceName, out var container))
            {
                return await WriteAsync(store,
                        row,
                        ExternalAppInstanceStatus.StoppedUnexpectedly,
                        ExternalAppInstanceEventKind.StoppedUnexpectedly,
                        ExternalAppFailureCategory.StoppedUnexpectedly,
                        $"The container for service '{serviceName}' is no longer on the container runtime.",
                        cancellationToken);
            }

            if (!IsRunning(container))
            {
                return await WriteAsync(store,
                        row,
                        ExternalAppInstanceStatus.StoppedUnexpectedly,
                        ExternalAppInstanceEventKind.StoppedUnexpectedly,
                        ExternalAppFailureCategory.StoppedUnexpectedly,
                        $"Service '{serviceName}' is {container.State}{ExitCodeSuffix(container)}.",
                        cancellationToken);
            }
        }

        return await AdoptOrRefuseAsync(store, runtime, daemonIsRootless, row, manifest, byService, cancellationToken);
    }

    /// <summary>
    ///     Every container is present and running, so the remaining question is whether they are the containers this
    ///     instance would create today. Reattachment VERIFIES before it trusts: a drifted container is running and
    ///     non-compliant, which is a policy failure rather than "stopped unexpectedly".
    /// </summary>
    private async Task<bool> AdoptOrRefuseAsync(IExternalAppInstanceStore store,
        IContainerRuntime runtime,
        bool daemonIsRootless,
        ExternalAppInstanceSnapshot row,
        ApplicationManifest manifest,
        IReadOnlyDictionary<string, ContainerSummary> byService,
        CancellationToken cancellationToken)
    {
        var identity = ExternalAppContainerIdentity.Resolve(daemonIsRootless, _options.ContainerIdentity);
        var plan = _service.TryPlanForVerification(row, manifest, ExternalAppService.ParseVariables(row.VariablesJson), identity);
        if (plan is null)
        {
            // Not a policy violation — nothing was observed to violate anything — and not "stopped unexpectedly"
            // either, because the containers are up. It is an instance this engine can no longer describe, and the
            // recovery is the rebuild a Start performs.
            return await WriteAsync(store,
                    row,
                    ExternalAppInstanceStatus.Failed,
                    ExternalAppInstanceEventKind.Failed,
                    ExternalAppFailureCategory.Unknown,
                    UnverifiablePlanSummary,
                    cancellationToken);
        }

        foreach (var service in plan.Services)
        {
            var container = byService[service.ServiceName];
            var inspection = await runtime.InspectAsync(container.Id, cancellationToken);

            if (!ExternalAppService.ImageMatches(inspection.Image, service.Specification.Image))
            {
                return await RefuseAsync(store,
                        row,
                        $"Service '{service.ServiceName}' is running an image this instance did not install.",
                        cancellationToken);
            }

            var violations = ApplicationContainerPolicy.FindViolations(service.Specification,
                inspection,
                daemonIsRootless,
                afterStart: true,
                _layout.Describe(row.Id).InstanceRoot);
            if (violations.Count > 0)
            {
                // The stored summary counts them and stops there — a violation can name a host path, and that column
                // is rendered in a browser. The node log is where the operator reads which checks failed.
                _logger.LogWarning("External application instance {InstanceId} service {Service} failed policy verification on boot: {Violations}.",
                    row.Id,
                    service.ServiceName,
                    string.Join("; ", violations));

                return await RefuseAsync(store,
                        row,
                        ExternalAppFailureTranslator.ForViolations(service.ServiceName, violations).Summary,
                        cancellationToken);
            }

            // Unhealthy rather than "not yet healthy": a healthcheck still inside its start period at boot is a
            // container that has not answered, and failing it here would turn a slow start into an instance the user
            // has to repair. The reuse path in ExternalAppService.FindReusableAsync draws the line in the same place.
            if (service.Specification.Healthcheck is not null && inspection.State.Health == ContainerHealthState.Unhealthy)
            {
                return await RefuseAsync(store, row, $"Service '{service.ServiceName}' reports itself unhealthy.", cancellationToken);
            }
        }

        return await WriteAsync(store,
                row,
                ExternalAppInstanceStatus.Running,
                ExternalAppInstanceEventKind.RestoredOnBoot,
                failureCategory: null,
                failureSummary: null,
                cancellationToken);
    }

    private Task<bool> RefuseAsync(IExternalAppInstanceStore store, ExternalAppInstanceSnapshot row, string summary, CancellationToken cancellationToken)
    {
        return WriteAsync(store,
            row,
            ExternalAppInstanceStatus.Failed,
            ExternalAppInstanceEventKind.Failed,
            ExternalAppFailureCategory.PolicyViolation,
            summary,
            cancellationToken);
    }

    /// <summary>
    ///     The user asked for this instance to be stopped and something is running it. Stopping is the one daemon
    ///     mutation this branch makes, and it is the mutation that honours the desired state rather than overriding it.
    /// </summary>
    private async Task<bool> StopStrayContainersAsync(IExternalAppInstanceStore store,
        IContainerRuntime runtime,
        ExternalAppInstanceSnapshot row,
        IReadOnlyDictionary<string, ContainerSummary> byService,
        CancellationToken cancellationToken)
    {
        var running = byService.Values.Where(IsRunning).ToList();
        if (running.Count == 0)
        {
            return false;
        }

        foreach (var container in running)
        {
            // The restart policy is never touched: a container the engine stopped through the daemon is recorded as
            // manually stopped and does not come back on its own.
            _ = await runtime.StopContainerAsync(container.Id, TimeSpan.FromSeconds(_options.StopGracePeriodSeconds), cancellationToken);
        }

        return await WriteAsync(store,
                row,
                ExternalAppInstanceStatus.Stopped,
                ExternalAppInstanceEventKind.Stopped,
                failureCategory: null,
                failureSummary: null,
                cancellationToken);
    }

    /// <summary>
    ///     Branch C. Removes only what carries THIS install id and an instance label no row claims. The install-id
    ///     filter is the security property: a container wearing the owner label under a different install id belongs
    ///     to another installation of this engine and is counted, never removed.
    ///     <para>
    ///         "No row claims it" is decided against the list this pass opened with, which an install admitted a
    ///         moment later is not in (R2-16). So an unclaimed instance is only removed once it is neither running
    ///         an operation, nor holding its gate, nor — re-read now rather than then — in the store: an install
    ///         whose row landed after that first read would otherwise have its containers taken out from under it.
    ///     </para>
    /// </summary>
    private async Task<int> RemoveOrphansAsync(IExternalAppInstanceStore store,
        IContainerRuntime runtime,
        IReadOnlyDictionary<Guid, List<ContainerSummary>> byInstance,
        IReadOnlyCollection<Guid> known,
        CancellationToken cancellationToken)
    {
        var removed = 0;

        foreach (var (instanceId, containers) in byInstance)
        {
            if (known.Contains(instanceId) || !await IsUnclaimedAsync(store, instanceId, cancellationToken))
            {
                continue;
            }

            _logger.LogWarning("Removing {Count} container(s) of external application instance {InstanceId}, which no row claims.",
                containers.Count,
                instanceId);

            // The same teardown the pipelines use, so there is one place that knows how a network is removed after
            // the containers attached to it.
            _ = await _service.RemoveInstanceContainersAsync(runtime, instanceId, cancellationToken);
            removed += containers.Count;
        }

        return removed;
    }

    /// <summary>
    ///     Whether an instance the pass's opening list did not know about is genuinely abandoned. The gate lease is
    ///     released immediately: it is asked as a question — is somebody working on this — never held across the
    ///     removal, which would make this pass the thing an install then waits behind.
    /// </summary>
    private async Task<bool> IsUnclaimedAsync(IExternalAppInstanceStore store, Guid instanceId, CancellationToken cancellationToken)
    {
        if (_runner.IsRunning(instanceId))
        {
            return false;
        }

        using (var lease = await _gate.TryEnterAsync(ExternalAppInstanceGate.InstanceKey(instanceId)))
        {
            if (lease is null)
            {
                return false;
            }
        }

        return await store.GetAsync(instanceId, cancellationToken) is null;
    }

    /// <summary>
    ///     A second list by the owner label ALONE. It exists because a moved data directory leaves containers that
    ///     this installation can neither see nor manage, still holding their loopback ports — invisible without this
    ///     count, and an unexplained "port already in use" on the next install.
    /// </summary>
    private async Task<int> CountForeignAsync(IContainerRuntime runtime, CancellationToken cancellationToken)
    {
        var owned = await runtime
                          .ListContainersDetailedAsync(new Dictionary<string, string>(StringComparer.Ordinal)
                              {
                                  [ExternalAppLabels.Owner] = ExternalAppLabels.OwnerValue
                              },
                              cancellationToken);

        var foreign = owned
                      .Where(container => !container.Labels.TryGetValue(ExternalAppLabels.Install, out var install)
                                          || !string.Equals(install, _service.InstallId, StringComparison.Ordinal))
                      .ToList();

        if (foreign.Count > 0)
        {
            _logger.LogWarning("{Count} container(s) carry this feature's owner label under a different installation id and are left alone; "
                               + "they were created by an engine whose data directory is not this one. Install ids seen: {InstallIds}.",
                foreign.Count,
                string.Join(", ", foreign.Select(static container =>
                                             container.Labels.TryGetValue(ExternalAppLabels.Install, out var install) ? install : "<none>")
                                         .Distinct(StringComparer.Ordinal)));
        }

        return foreign.Count;
    }

    private static Dictionary<Guid, List<ContainerSummary>> GroupByInstance(IReadOnlyList<ContainerSummary> containers)
    {
        var grouped = new Dictionary<Guid, List<ContainerSummary>>();

        foreach (var container in containers)
        {
            if (!container.Labels.TryGetValue(ExternalAppLabels.Instance, out var value)
                || !Guid.TryParseExact(value, "N", out var instanceId))
            {
                continue;
            }

            if (!grouped.TryGetValue(instanceId, out var list))
            {
                list = [];
                grouped[instanceId] = list;
            }

            list.Add(container);
        }

        return grouped;
    }

    private static Dictionary<string, ContainerSummary> ByService(IReadOnlyList<ContainerSummary> containers)
    {
        var byService = new Dictionary<string, ContainerSummary>(StringComparer.Ordinal);

        foreach (var container in containers)
        {
            if (container.Labels.TryGetValue(ExternalAppLabels.Service, out var serviceName))
            {
                byService[serviceName] = container;
            }
        }

        return byService;
    }

    private static bool IsRunning(ContainerSummary container)
    {
        return string.Equals(container.State, "running", StringComparison.Ordinal);
    }

    private static string ExitCodeSuffix(ContainerSummary container)
    {
        // Absent is a real answer from a list response that carries no exit-code field, and rendering it as 0 would
        // report a crash as a clean exit.
        return container.ExitCode is { } exitCode
            ? string.Create(CultureInfo.InvariantCulture, $" with exit code {exitCode}")
            : string.Empty;
    }

    /// <summary>
    ///     One compare-and-swap addressed at the row as it was read, plus the event it mints and the ping. A LOST
    ///     swap is ignored on purpose: another writer moved the row after the list was taken, and its verdict is the
    ///     newer one.
    /// </summary>
    private async Task<bool> WriteAsync(IExternalAppInstanceStore store,
        ExternalAppInstanceSnapshot row,
        ExternalAppInstanceStatus newStatus,
        ExternalAppInstanceEventKind kind,
        ExternalAppFailureCategory? failureCategory,
        string? failureSummary,
        CancellationToken cancellationToken)
    {
        var result = await store.UpdateStatusAsync(new ExternalAppStatusUpdate(row.Id,
                                        row.Version,
                                        new HashSet<ExternalAppInstanceStatus>
                                        {
                                            row.Status
                                        },
                                        newStatus,
                                        kind,
                                        EventDetailJson: null,
                                        _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                                        StoppedAtUtc: newStatus == ExternalAppInstanceStatus.Stopped
                                            ? _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
                                            : null,
                                        FailureCategory: failureCategory,
                                        FailureSummary: failureSummary,
                                        ClearFailure: failureCategory is null),
                                    cancellationToken);

        if (!result.Applied)
        {
            _logger.LogWarning("The boot verdict {Status} for external application instance {InstanceId} lost its compare-and-swap; another writer moved the row.",
                newStatus,
                row.Id);
            return false;
        }

        await PublishAsync(row.Id, result.Sequence, kind, newStatus);
        return true;
    }

    /// <summary>What one row's pass did, so the caller's three counters are decided in one place.</summary>
    private enum RowVerdict
    {
        Unchanged = 0,
        Changed = 1,
        Busy = 2
    }

    private async Task PublishAsync(Guid instanceId, long sequence, ExternalAppInstanceEventKind kind, ExternalAppInstanceStatus status)
    {
        try
        {
            await _publisher.PublishAsync(instanceId, sequence, kind, status, CancellationToken.None);
        }
#pragma warning disable CA1031 // A subscriber that cannot be reached must not fail the pass whose result it describes.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            _logger.LogDebug(exception, "Publishing the {Kind} event for external application instance {InstanceId} failed.", kind, instanceId);
        }
    }
}
