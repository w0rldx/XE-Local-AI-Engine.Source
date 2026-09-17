namespace XE_Local_AI_Engine.Client.Services.ExternalApps.Implementation;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Containers;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container;

/// <summary>
///     Start, stop, restart, reset, uninstall, configure and cancel: the state machine, the shared admission, and
///     the reuse-or-rebuild decision that makes a start honest about what it is starting.
/// </summary>
internal sealed partial class ExternalAppService
{
    private const string UiPortRole = "ui";

    /// <summary>The four statuses a user-initiated operation may act on. The six transients accept nothing but cancel.</summary>
    private static readonly ExternalAppInstanceStatus[] Operable =
    [
        ExternalAppInstanceStatus.Stopped,
        ExternalAppInstanceStatus.Running,
        ExternalAppInstanceStatus.Failed,
        ExternalAppInstanceStatus.StoppedUnexpectedly
    ];

    /// <summary>The three long transients a user can be stuck behind, and the only ones a cancel is accepted on.</summary>
    private static readonly ExternalAppInstanceStatus[] Cancellable =
    [
        ExternalAppInstanceStatus.Installing,
        ExternalAppInstanceStatus.Updating,
        ExternalAppInstanceStatus.Resetting
    ];

    public Task<ExternalAppInstanceSummary> StartAsync(Guid instanceId, long expectedVersion, CancellationToken cancellationToken = default)
    {
        return AdmitAsync(ExternalAppOperationKind.Start,
            instanceId,
            expectedVersion,
            RunStartAsync,
            cancellationToken);
    }

    public Task<ExternalAppInstanceSummary> StopAsync(Guid instanceId, long expectedVersion, CancellationToken cancellationToken = default)
    {
        return AdmitAsync(ExternalAppOperationKind.Stop, instanceId, expectedVersion, RunStopAsync, cancellationToken);
    }

    public Task<ExternalAppInstanceSummary> RestartAsync(Guid instanceId, long expectedVersion, CancellationToken cancellationToken = default)
    {
        return AdmitAsync(ExternalAppOperationKind.Restart, instanceId, expectedVersion, RunRestartAsync, cancellationToken);
    }

    public Task<ExternalAppInstanceSummary> ResetAsync(Guid instanceId, long expectedVersion, CancellationToken cancellationToken = default)
    {
        return AdmitAsync(ExternalAppOperationKind.Reset, instanceId, expectedVersion, RunResetAsync, cancellationToken);
    }

    public Task<ExternalAppInstanceSummary> UninstallAsync(Guid instanceId, long expectedVersion, CancellationToken cancellationToken = default)
    {
        return AdmitAsync(ExternalAppOperationKind.Uninstall, instanceId, expectedVersion, RunUninstallAsync, cancellationToken);
    }

    public async Task<ExternalAppInstanceDetail> ConfigureAsync(Guid instanceId,
        long expectedVersion,
        IReadOnlyDictionary<string, string> variables,
        CancellationToken cancellationToken = default)
    {
        EnsureEnabled();
        ArgumentNullException.ThrowIfNull(variables);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var services = ScopedServices.From(scope.ServiceProvider);

        using var lease = await _gate.TryEnterAsync(ExternalAppInstanceGate.InstanceKey(instanceId))
                          ?? throw new ExternalAppOperationInFlightException("An operation is already running on this instance.");

        var row = await RequireInstanceAsync(services.Store, instanceId, cancellationToken);
        if (row.Status == ExternalAppInstanceStatus.Running || Array.IndexOf(Operable, row.Status) < 0)
        {
            throw new ExternalAppInvalidTransitionException($"Settings can only be changed while the application is not running; it is {row.Status}.");
        }

        RequireVersion(row, expectedVersion);

        var manifest = DeserializeManifest(row.ManifestSnapshotJson);
        var merged = Merge(ParseVariables(row.VariablesJson), variables);
        var accepted = ValidateVariables(manifest, merged);

        // Sets NeedsRecreate: a created container's environment is immutable, so the new values take effect on the
        // next start, which rebuilds because of that flag. No event and no container work — nothing happened yet.
        if (!await services.Store.UpdateVariablesAsync(instanceId, expectedVersion, SerializeVariables(accepted), Now(), cancellationToken))
        {
            throw new ExternalAppConcurrencyException("The instance changed while these settings were being saved.");
        }

        var updated = await RequireInstanceAsync(services.Store, instanceId, cancellationToken);
        var versions = await ReadCatalogVersionsAsync(services.Catalog, cancellationToken);
        return ToDetail(updated, ToSummary(updated, versions));
    }

    public async Task CancelAsync(Guid instanceId, CancellationToken cancellationToken = default)
    {
        EnsureEnabled();

        await using var scope = _scopeFactory.CreateAsyncScope();
        var services = ScopedServices.From(scope.ServiceProvider);

        // No gate: the gate is held by the operation being cancelled, and taking it is exactly what must not happen.
        var row = await RequireInstanceAsync(services.Store, instanceId, cancellationToken);
        if (Array.IndexOf(Cancellable, row.Status) < 0)
        {
            throw new ExternalAppInvalidTransitionException($"An operation in status {row.Status} cannot be cancelled.");
        }

        if (!_runner.Cancel(instanceId))
        {
            // A transient status with no live operation is a CRASHED one. The boot reconciler settles those; a
            // cancel that pretended to stop something would leave the row exactly as stuck as it found it.
            throw new ExternalAppInvalidTransitionException("No operation is running on this instance.");
        }
    }

    /// <summary>
    ///     The shared admission every lifecycle verb runs: the gate, the row, the version, the transition table, the
    ///     compare-and-swap into the transient status, and the hand-over of the lease to the background operation.
    /// </summary>
    private async Task<ExternalAppInstanceSummary> AdmitAsync(ExternalAppOperationKind kind,
        Guid instanceId,
        long expectedVersion,
        Func<IServiceProvider, LifecycleContext, CancellationToken, Task> pipeline,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();

        await using var scope = _scopeFactory.CreateAsyncScope();
        var services = ScopedServices.From(scope.ServiceProvider);

        IDisposable? lease = null;
        try
        {
            lease = await _gate.TryEnterAsync(ExternalAppInstanceGate.InstanceKey(instanceId))
                    ?? throw new ExternalAppOperationInFlightException("An operation is already running on this instance.");

            var row = await RequireInstanceAsync(services.Store, instanceId, cancellationToken);
            RequireVersion(row, expectedVersion);

            var versions = await ReadCatalogVersionsAsync(services.Catalog, cancellationToken);
            if (kind == ExternalAppOperationKind.Stop && row.Status == ExternalAppInstanceStatus.Stopped)
            {
                // Idempotent success. The desired state is already Stopped — a row cannot be Stopped while wanting
                // to run — so re-asserting it would write an event saying nothing happened.
                return ToSummary(row, versions);
            }

            var admitted = AdmittedStatusFor(kind, row.Status)
                           ?? throw new ExternalAppInvalidTransitionException($"{kind} is not available while the application is {row.Status}.");

            // After the transition table, so a Start of something already Running still reads as the invalid
            // transition it is; before the compare-and-swap, so the row never enters Starting for a plan this node
            // cannot build. A bridge that closed after the install — a configuration change, a port collision at
            // boot — would otherwise fail inside the pipeline on the unresolvable built-in and settle the row Failed,
            // where install and update refuse the same state with the same 400. Stop, Reset, Uninstall, Cancel and
            // Configure stay ungated on purpose: an operator must be able to shut down and clear an application on a
            // node that has lost its bridge.
            if (kind is ExternalAppOperationKind.Start or ExternalAppOperationKind.Restart && BridgeUnavailableFor(row))
            {
                throw Refuse(ExternalAppBlockedReason.BridgeUnavailable, BridgeUnavailableDetail);
            }

            var cursor = new InstanceCursor(instanceId, row.Version, row.Status);
            if (!await ApplyAsync(services.Store, cursor, Transition(cursor, admitted, RequestEventFor(kind, row.Status)), cancellationToken))
            {
                throw new ExternalAppConcurrencyException("The instance changed while this command was being admitted.");
            }

            var context = new LifecycleContext(instanceId, cursor.Version, cursor.Sequence, admitted, row);
            if (!_runner.TryStart(instanceId, kind, lease, (provider, token) => pipeline(provider, context, token), out _))
            {
                throw new ExternalAppOperationInFlightException("An operation is already running on this instance.");
            }

            lease = null;

            return ToSummary(row with
            {
                Status = admitted,
                Version = cursor.Version,
                UpdatedAtUtc = Now()
            }, versions);
        }
        finally
        {
            lease?.Dispose();
        }
    }

    private static void RequireVersion(ExternalAppInstanceSnapshot row, long expectedVersion)
    {
        if (row.Version != expectedVersion)
        {
            // V1 has no durable idempotency keys. This is what stops a retried reset from wiping data the first one
            // created between the two attempts.
            throw new ExternalAppConcurrencyException("This application changed since it was last read; refresh and try again.");
        }
    }

    /// <summary>The transition table of the state machine. <see langword="null" /> is an invalid transition.</summary>
    private static ExternalAppInstanceStatus? AdmittedStatusFor(ExternalAppOperationKind kind, ExternalAppInstanceStatus current)
    {
        if (Array.IndexOf(Operable, current) < 0)
        {
            return null;
        }

        return kind switch
        {
            ExternalAppOperationKind.Start when current != ExternalAppInstanceStatus.Running => ExternalAppInstanceStatus.Starting,
            ExternalAppOperationKind.Stop when current != ExternalAppInstanceStatus.Stopped => ExternalAppInstanceStatus.Stopping,
            // A restart of something that is running begins by stopping it; a restart of something that is not is a
            // start, and reporting Stopping for it would show a progress step that never happens.
            ExternalAppOperationKind.Restart => current == ExternalAppInstanceStatus.Running
                ? ExternalAppInstanceStatus.Stopping
                : ExternalAppInstanceStatus.Starting,
            ExternalAppOperationKind.Update => ExternalAppInstanceStatus.Updating,
            ExternalAppOperationKind.Reset => ExternalAppInstanceStatus.Resetting,
            ExternalAppOperationKind.Uninstall => ExternalAppInstanceStatus.Uninstalling,
            _ => null
        };
    }

    private static ExternalAppInstanceEventKind RequestEventFor(ExternalAppOperationKind kind, ExternalAppInstanceStatus current)
    {
        return kind switch
        {
            ExternalAppOperationKind.Start => ExternalAppInstanceEventKind.StartRequested,
            ExternalAppOperationKind.Stop => ExternalAppInstanceEventKind.StopRequested,
            ExternalAppOperationKind.Restart => current == ExternalAppInstanceStatus.Running
                ? ExternalAppInstanceEventKind.StopRequested
                : ExternalAppInstanceEventKind.StartRequested,
            ExternalAppOperationKind.Update => ExternalAppInstanceEventKind.UpdateRequested,
            ExternalAppOperationKind.Reset => ExternalAppInstanceEventKind.ResetRequested,
            _ => ExternalAppInstanceEventKind.UninstallRequested
        };
    }

    private static IReadOnlyDictionary<string, string> Merge(IReadOnlyDictionary<string, string> stored,
        IReadOnlyDictionary<string, string> incoming)
    {
        var merged = new Dictionary<string, string>(stored, StringComparer.Ordinal);
        foreach (var entry in incoming)
        {
            if (string.Equals(entry.Value, ExternalAppVariableMask.Value, StringComparison.Ordinal))
            {
                // The sentinel the node masked with on the way out means "keep what is stored". Treating it as a
                // value would write the placeholder over the password it was standing in for.
                continue;
            }

            merged[entry.Key] = entry.Value;
        }

        return merged;
    }

    private async Task RunStartAsync(IServiceProvider provider, LifecycleContext context, CancellationToken cancellationToken)
    {
        var services = ScopedServices.From(provider);
        var cursor = context.ToCursor();
        IContainerRuntime? runtime = null;

        try
        {
            var resolution = await services.Resolver.ResolveAsync(cancellationToken: cancellationToken);
            runtime = await services.Resolver.CreateRuntimeAsync(cancellationToken: cancellationToken);

            var published = await StartInstanceAsync(runtime, resolution.Daemon.IsRootless, context.Row, cancellationToken);

            _ = await ApplyAsync(services.Store,
                    cursor,
                    Transition(cursor, ExternalAppInstanceStatus.Running, ExternalAppInstanceEventKind.Started) with
                    {
                        DesiredState = ExternalAppDesiredState.Running,
                        PublishedPortsJson = ExternalAppPublishedPorts.Serialize(published),
                        StartedAtUtc = Now(),
                        NeedsRecreate = false,
                        ClearFailure = true
                    },
                    cancellationToken);
        }
        catch (Exception exception)
        {
            await SettleFailureAsync(services.Store, runtime, cursor, exception);
        }
        finally
        {
            await DisposeRuntimeAsync(runtime);
        }
    }

    private async Task RunStopAsync(IServiceProvider provider, LifecycleContext context, CancellationToken cancellationToken)
    {
        var services = ScopedServices.From(provider);
        var cursor = context.ToCursor();
        IContainerRuntime? runtime = null;

        try
        {
            runtime = await services.Resolver.CreateRuntimeAsync(cancellationToken: cancellationToken);
            await StopInstanceAsync(runtime, context.Row, cancellationToken);

            _ = await ApplyAsync(services.Store,
                    cursor,
                    Transition(cursor, ExternalAppInstanceStatus.Stopped, ExternalAppInstanceEventKind.Stopped) with
                    {
                        DesiredState = ExternalAppDesiredState.Stopped,
                        StoppedAtUtc = Now(),
                        ClearFailure = true
                    },
                    cancellationToken);
        }
        catch (Exception exception)
        {
            await SettleFailureAsync(services.Store, runtime, cursor, exception);
        }
        finally
        {
            await DisposeRuntimeAsync(runtime);
        }
    }

    private async Task RunRestartAsync(IServiceProvider provider, LifecycleContext context, CancellationToken cancellationToken)
    {
        var services = ScopedServices.From(provider);
        var cursor = context.ToCursor();
        IContainerRuntime? runtime = null;

        try
        {
            var resolution = await services.Resolver.ResolveAsync(cancellationToken: cancellationToken);
            runtime = await services.Resolver.CreateRuntimeAsync(cancellationToken: cancellationToken);

            // Stop then start under ONE gate hold, so nothing can slip an operation between the halves.
            await StopInstanceAsync(runtime, context.Row, cancellationToken);
            var published = await StartInstanceAsync(runtime, resolution.Daemon.IsRootless, context.Row, cancellationToken);

            _ = await ApplyAsync(services.Store,
                    cursor,
                    Transition(cursor, ExternalAppInstanceStatus.Running, ExternalAppInstanceEventKind.Restarted) with
                    {
                        DesiredState = ExternalAppDesiredState.Running,
                        PublishedPortsJson = ExternalAppPublishedPorts.Serialize(published),
                        StartedAtUtc = Now(),
                        NeedsRecreate = false,
                        ClearFailure = true
                    },
                    cancellationToken);
        }
        catch (Exception exception)
        {
            await SettleFailureAsync(services.Store, runtime, cursor, exception);
        }
        finally
        {
            await DisposeRuntimeAsync(runtime);
        }
    }

    private async Task RunResetAsync(IServiceProvider provider, LifecycleContext context, CancellationToken cancellationToken)
    {
        var services = ScopedServices.From(provider);
        var cursor = context.ToCursor();
        IContainerRuntime? runtime = null;

        try
        {
            var resolution = await services.Resolver.ResolveAsync(cancellationToken: cancellationToken);
            runtime = await services.Resolver.CreateRuntimeAsync(cancellationToken: cancellationToken);

            await StopInstanceAsync(runtime, context.Row, cancellationToken);

            // Confirmed before the wipe, never assumed: a surviving container is still writing to the volumes this
            // line is about to delete, and the rebuild below would then create its replacement beside it.
            await RequireTeardownAsync(runtime, context.InstanceId, cancellationToken);

            // The contents go from INSIDE a container and the empty tree goes from here. An application that ran as
            // its own non-root user left directories this process can neither traverse nor unlink, and a host-side
            // recursive delete on them fails with the storage error this reset used to report.
            await WipeVolumeContentsAsync(runtime, context.InstanceId, cancellationToken);
            WipeVolumes(context.InstanceId);

            var manifest = DeserializeManifest(context.Row.ManifestSnapshotJson);
            var published = await RebuildAsync(runtime,
                    resolution.Daemon.IsRootless,
                    context.InstanceId,
                    manifest,
                    ParseVariables(context.Row.VariablesJson),
                    BridgeGrantFor(context.Row.BridgeToken),
                    commitBeforeStart: null,
                    cancellationToken);

            // The rebuild leaves everything running. An instance that was stopped before the reset is stopped again:
            // the desired state is the user's, and a reset is not a decision to start something.
            var restoreStopped = context.Row.DesiredState == ExternalAppDesiredState.Stopped;
            if (restoreStopped)
            {
                await StopInstanceAsync(runtime, context.Row, cancellationToken);
            }

            _ = await ApplyAsync(services.Store,
                    cursor,
                    Transition(cursor,
                            restoreStopped ? ExternalAppInstanceStatus.Stopped : ExternalAppInstanceStatus.Running,
                            ExternalAppInstanceEventKind.Reset) with
                        {
                            DesiredState = context.Row.DesiredState,
                            PublishedPortsJson = ExternalAppPublishedPorts.Serialize(published),
                            NeedsRecreate = false,
                            ClearFailure = true
                        },
                    cancellationToken);
        }
        catch (Exception exception)
        {
            // A failed wipe settles to Failed rather than leaving the row in Resetting, where every operation —
            // including another reset — answers 409 forever.
            await SettleFailureAsync(services.Store, runtime, cursor, exception);
        }
        finally
        {
            await DisposeRuntimeAsync(runtime);
        }
    }

    private async Task RunUninstallAsync(IServiceProvider provider, LifecycleContext context, CancellationToken cancellationToken)
    {
        var services = ScopedServices.From(provider);
        var cursor = context.ToCursor();
        IContainerRuntime? runtime = null;

        try
        {
            runtime = await services.Resolver.CreateRuntimeAsync(cancellationToken: cancellationToken);
            await StopInstanceAsync(runtime, context.Row, cancellationToken);

            // Nothing below this line is recoverable: deleting the row while a container of this instance survives
            // would leave it running with no row to act on it, and the next boot pass would only see an orphan.
            await RequireTeardownAsync(runtime, context.InstanceId, cancellationToken);

            // The data goes BEFORE the row, and the order is binding: an uninstall that removed the row while an
            // application's own 0700 directories were still on disk answered a confirmation promising deletion with
            // silence, and left nothing behind that could finish the job. A wipe that fails throws from here, so the
            // row stays and settles Failed with a retry that reaches this same line again.
            await WipeVolumeContentsAsync(runtime, context.InstanceId, cancellationToken);

            if (!DeleteStorage(context.InstanceId))
            {
                // Still best-effort, and the row still goes: an empty directory tree left by a full disk or a
                // permission the operator changed by hand is recoverable with one command, a row the UI is stuck on
                // is not.
                _logger.LogWarning("External application instance {InstanceId} was uninstalled but its data directory '{StoragePath}' could not be removed; it can be deleted by hand.",
                    context.InstanceId,
                    context.Row.StoragePath);
            }

            if (!await services.Store.DeleteAsync(context.InstanceId, cursor.Version, CancellationToken.None))
            {
                throw new ExternalAppPipelineException(new ExternalAppFailure(ExternalAppFailureCategory.Unknown,
                    "This application changed while it was being uninstalled."));
            }

            // Published after the rows are gone and never replayable, which is correct: there is nothing left to
            // replay it from, and a subscriber's only sensible response is to re-read and find the instance absent.
            await PublishAsync(context.InstanceId,
                    cursor.Sequence + 1,
                    ExternalAppInstanceEventKind.Uninstalled,
                    ExternalAppInstanceStatus.Uninstalling);

            _gate.Forget(ExternalAppInstanceGate.InstanceKey(context.InstanceId));
        }
        catch (Exception exception)
        {
            await SettleFailureAsync(services.Store, runtime, cursor, exception);
        }
        finally
        {
            await DisposeRuntimeAsync(runtime);
        }
    }

    /// <summary>
    ///     Reuse or rebuild, decided by VERIFYING rather than by counting: a container that is missing, stale,
    ///     unverifiable or built from a different image is not a container this instance can be started from.
    /// </summary>
    private async Task<IReadOnlyList<ExternalAppPublishedPort>> StartInstanceAsync(IContainerRuntime runtime,
        bool daemonIsRootless,
        ExternalAppInstanceSnapshot row,
        CancellationToken cancellationToken)
    {
        var manifest = DeserializeManifest(row.ManifestSnapshotJson);
        var variables = ParseVariables(row.VariablesJson);
        var identity = ExternalAppContainerIdentity.Resolve(daemonIsRootless, _options.ContainerIdentity);
        var plan = TryPlanForVerification(row, manifest, variables, identity);

        var existing = plan is null
            ? null
            : await FindReusableAsync(runtime, daemonIsRootless, row, plan, cancellationToken);

        if (plan is null || existing is null)
        {
            await RequireTeardownAsync(runtime, row.Id, cancellationToken);
            return await RebuildAsync(runtime, daemonIsRootless, row.Id, manifest, variables, BridgeGrantFor(row.BridgeToken), commitBeforeStart: null, cancellationToken);
        }

        try
        {
            return await StartAllAsync(runtime, daemonIsRootless, row.Id, manifest, plan, existing, cancellationToken);
        }
        catch (ExternalAppPipelineException failure) when (failure.Failure.Category == ExternalAppFailureCategory.PortUnavailable)
        {
            // The port these containers were built around belongs to something else now, and a container's published
            // port is fixed at create. Rebuilding is the only way to give it one that is free.
            _logger.LogWarning("Reusing the containers of external application instance {InstanceId} lost a host port; rebuilding.", row.Id);
            await RequireTeardownAsync(runtime, row.Id, CancellationToken.None);
            return await RebuildAsync(runtime, daemonIsRootless, row.Id, manifest, variables, BridgeGrantFor(row.BridgeToken), commitBeforeStart: null, cancellationToken);
        }
    }

    /// <summary>
    ///     The container of each service, keyed by service name, or <see langword="null" /> when anything about the
    ///     set means the instance has to be rebuilt.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>?> FindReusableAsync(IContainerRuntime runtime,
        bool daemonIsRootless,
        ExternalAppInstanceSnapshot row,
        DeploymentPlan plan,
        CancellationToken cancellationToken)
    {
        if (row.NeedsRecreate)
        {
            // Settings changed since these containers were created, and a container's environment is immutable.
            return null;
        }

        var listed = await runtime.ListContainersDetailedAsync(ExternalAppLabels.For(_installId, row.Id), cancellationToken);
        var byService = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var summary in listed)
        {
            if (summary.Labels.TryGetValue(ExternalAppLabels.Service, out var serviceName))
            {
                byService[serviceName] = summary.Id;
            }
        }

        var reusable = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var service in plan.Services)
        {
            if (!byService.TryGetValue(service.ServiceName, out var containerId))
            {
                // Starting the surviving subset and reporting Running would call a broken application healthy.
                return null;
            }

            var inspection = await runtime.InspectAsync(containerId, cancellationToken);
            if (!ImageMatches(inspection.Image, service.Specification.Image))
            {
                return null;
            }

            if (ApplicationContainerPolicy.FindViolations(service.Specification,
                                              inspection,
                                              daemonIsRootless,
                                              inspection.State.Running,
                                              _layout.Describe(row.Id).InstanceRoot)
                                          .Count > 0)
            {
                return null;
            }

            if (service.Specification.Healthcheck is not null
                && inspection.State.Running
                && inspection.State.Health == ContainerHealthState.Unhealthy)
            {
                return null;
            }

            reusable[service.ServiceName] = containerId;
        }

        return reusable;
    }

    /// <summary>
    ///     Rebuilds the plan around the host ports the instance ALREADY has, so verifying an existing container does
    ///     not fail on a port the allocator would have picked differently this time.
    /// </summary>
    internal DeploymentPlan? TryPlanForVerification(ExternalAppInstanceSnapshot row,
        ApplicationManifest manifest,
        IReadOnlyDictionary<string, string> variables,
        ResolvedContainerIdentity identity)
    {
        var stored = ExternalAppPublishedPorts.Parse(row.PublishedPortsJson);
        var declared = manifest.Services
                               .SelectMany(service => service.Ports
                                                             .Where(port => string.Equals(port.Role, UiPortRole, StringComparison.Ordinal))
                                                             .Select(port => (service.Name, port.ContainerPort)))
                               .ToList();

        if (declared.Count != stored.Count)
        {
            return null;
        }

        var hostPorts = new List<ExternalAppHostPort>(declared.Count);
        foreach (var (serviceName, containerPort) in declared)
        {
            var match = stored.FirstOrDefault(port => string.Equals(port.Service, serviceName, StringComparison.Ordinal)
                                                      && port.ContainerPort == containerPort);
            if (match is null)
            {
                return null;
            }

            hostPorts.Add(new ExternalAppHostPort(serviceName, containerPort, match.HostPort));
        }

        try
        {
            // The SAME grant the rebuild path injects. A verification plan without it would describe an
            // environment the running containers do not have, and Start would rebuild containers that were fine.
            return DeploymentPlanner.Plan(manifest,
                row.Id,
                _installId,
                variables,
                identity,
                hostPorts,
                _layout.Describe(row.Id),
                BridgeGrantFor(row.BridgeToken));
        }
        catch (Exception exception) when (exception is ExternalAppConfigurationException or ExternalAppManifestException or ContainerPolicyException)
        {
            // The stored snapshot cannot be planned as it stands. A rebuild re-plans it from scratch and fails
            // loudly there, where the phase is recorded, rather than silently reusing containers nobody verified.
            // The policy refusal belongs here too: a snapshot whose image lost its digest pin, or that names a
            // capability the allow-list no longer grants, is unplannable rather than a reason to abandon the
            // caller's whole pass — and on the boot pass every later row would have lost its verdict with it.
            _logger.LogWarning(exception,
                "The stored snapshot of external application instance {InstanceId} could not be planned for verification; rebuilding.",
                row.Id);
            return null;
        }
    }

    private Task StopInstanceAsync(IContainerRuntime runtime, ExternalAppInstanceSnapshot row, CancellationToken cancellationToken)
    {
        return StopInstanceAsync(runtime, row.Id, DeserializeManifest(row.ManifestSnapshotJson), cancellationToken);
    }

    /// <summary>
    ///     The same stop against a NAMED manifest, for the one caller whose containers are no longer the row's:
    ///     after an update has rebuilt the instance, the target manifest is what says which services exist and in
    ///     what order they come down. Stopping by the installed snapshot would leave a service the target added
    ///     running under its restart policy while the row says the application is stopped.
    /// </summary>
    private async Task StopInstanceAsync(IContainerRuntime runtime,
        Guid instanceId,
        ApplicationManifest manifest,
        CancellationToken cancellationToken)
    {
        var order = StopOrder(manifest);
        var listed = await runtime.ListContainersDetailedAsync(ExternalAppLabels.For(_installId, instanceId), cancellationToken);

        var byService = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var summary in listed)
        {
            if (summary.Labels.TryGetValue(ExternalAppLabels.Service, out var serviceName))
            {
                byService[serviceName] = summary.Id;
            }
        }

        foreach (var serviceName in order)
        {
            if (!byService.TryGetValue(serviceName, out var containerId))
            {
                continue;
            }

            try
            {
                // The restart policy is never touched. It was created unless-stopped and stays that way: a container
                // the engine stopped through the daemon is recorded as manually stopped and does not come back.
                _ = await runtime.StopContainerAsync(containerId, TimeSpan.FromSeconds(_options.StopGracePeriodSeconds), cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // There is no Stop phase: the translator's Start arm is the one whose mapping fits — a daemon that
                // cannot serve us is RuntimeUnavailable, anything else is Unknown.
                throw Failed(ExternalAppFailurePhase.Start, exception);
            }
        }
    }

    /// <summary>Dependency order reversed: a dependant is stopped before the thing it depends on.</summary>
    private IReadOnlyList<string> StopOrder(ApplicationManifest manifest)
    {
        IEnumerable<string> ordered;
        try
        {
            ordered = DeploymentPlanner.ServiceOrder(manifest);
        }
        catch (ExternalAppManifestException exception)
        {
            // A snapshot whose dependencies no longer form an order. The manifest's own order is the best available,
            // and stopping in the wrong order costs a grace period, never data.
            _logger.LogWarning(exception, "The stored snapshot of external application '{ApplicationId}' could not be ordered for a stop; using its declared order.",
                manifest.Id);
            ordered = manifest.Services.Select(static service => service.Name);
        }

        return [.. ordered.Reverse()];
    }

    private void WipeVolumes(Guid instanceId)
    {
        try
        {
            _layout.DeleteVolumes(instanceId);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Failed(ExternalAppFailurePhase.Storage, exception);
        }
    }

    /// <summary>The one catch every lifecycle pipeline funnels into, so a failure is a row state rather than an escape.</summary>
    private async Task SettleFailureAsync(IExternalAppInstanceStore store,
        IContainerRuntime? runtime,
        InstanceCursor cursor,
        Exception exception)
    {
        if (exception is OperationCanceledException && _runner.IsShuttingDown)
        {
            LeaveTransientForShutdown(cursor.InstanceId);
            return;
        }

        var failure = exception switch
        {
            ExternalAppPipelineException pipeline => pipeline.Failure,
            OperationCanceledException => Cancelled(),
            _ => ExternalAppFailureTranslator.Translate(ExternalAppFailurePhase.Resolution, exception)
        };

        await FailAsync(store, runtime, cursor, failure);
    }

    private static async Task DisposeRuntimeAsync(IContainerRuntime? runtime)
    {
        if (runtime is not null)
        {
            await runtime.DisposeAsync();
        }
    }

    /// <summary>What admission decided, handed to the background operation as values rather than as shared state.</summary>
    private sealed record LifecycleContext(
        Guid InstanceId,
        long Version,
        long Sequence,
        ExternalAppInstanceStatus Status,
        ExternalAppInstanceSnapshot Row)
    {
        public InstanceCursor ToCursor()
        {
            return new InstanceCursor(InstanceId, Version, Status)
            {
                Sequence = Sequence
            };
        }
    }
}
