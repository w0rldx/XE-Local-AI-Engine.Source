namespace XE_Local_AI_Engine.Client.Services.ExternalApps.Implementation;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Containers;
using XE_Local_AI_Engine.Client.Services.Containers.Bridge;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container;

/// <summary>
///     The rebuild block and the writes every pipeline shares. Install, start-with-a-rebuild, reset and update all
///     re-enter <see cref="RebuildAsync" />, which is why the image pull sits inside it rather than in install: by
///     the time a stopped instance is started again its image may be gone.
/// </summary>
internal sealed partial class ExternalAppService
{
    /// <summary>Where the storage helper's one bind mount appears inside it.</summary>
    private const string HelperMountPath = "/storage";

    /// <summary>
    ///     The storage helper's whole command: remove the CONTENTS of the mounted directory and never the mount point
    ///     itself, which the host-side delete that follows still needs. A compile-time constant, so no engine-side
    ///     value — no instance id, no path — is ever interpolated into a shell string.
    /// </summary>
    private const string StorageHelperCommand = "find " + HelperMountPath + " -mindepth 1 -delete";

    /// <summary>
    ///     The isolated network mode the storage helper runs in. Docker's own predefined <c>none</c> network: no
    ///     interface but loopback, and nothing it could reach if its command were something other than what it is.
    /// </summary>
    private const string IsolatedNetworkName = "none";

    /// <summary>
    ///     The helper's process ceiling. It runs one shell and one <c>find</c>; a manifest's figure would be the
    ///     wrong number here, because this container is the engine's and not the application's.
    /// </summary>
    private const long StorageHelperPidsLimit = 64;

    /// <summary>The six statuses an operation is still in flight in. Only from one of these does a failure write belong.</summary>
    private static readonly ExternalAppInstanceStatus[] Transient =
    [
        ExternalAppInstanceStatus.Installing,
        ExternalAppInstanceStatus.Starting,
        ExternalAppInstanceStatus.Stopping,
        ExternalAppInstanceStatus.Updating,
        ExternalAppInstanceStatus.Resetting,
        ExternalAppInstanceStatus.Uninstalling
    ];

    /// <summary>
    ///     Pull, network, ports, plan, storage, create-and-verify, start-and-verify, read back. The one path that
    ///     turns a manifest plus its variables into running containers.
    /// </summary>
    /// <param name="bridgeGrant">
    ///     This instance's bridge grant, or <see langword="null" /> when it has none. The CALLER resolves it, and
    ///     deliberately: update mints a token for a row installed before the bridge existed and commits it inside the
    ///     rebuild, so a grant read from the row here would be the null this rebuild is in the middle of replacing.
    ///     It is also resolved once per rebuild rather than per attempt — a replan that minted a different grant
    ///     would leave sibling containers pointing at a credential the engine no longer expects.
    /// </param>
    /// <param name="commitBeforeStart">
    ///     Update's transactional commit, run after every replacement container is created and verified and before
    ///     any is started, and given the ports the plan chose. Null for every other caller.
    /// </param>
    private async Task<IReadOnlyList<ExternalAppPublishedPort>> RebuildAsync(IContainerRuntime runtime,
        bool daemonIsRootless,
        Guid instanceId,
        ApplicationManifest manifest,
        IReadOnlyDictionary<string, string> variables,
        ContainerBridgeGrant? bridgeGrant,
        Func<IReadOnlyList<ExternalAppPublishedPort>, CancellationToken, Task>? commitBeforeStart,
        CancellationToken cancellationToken)
    {
        await PullImagesAsync(runtime, instanceId, manifest, cancellationToken);

        var identity = ExternalAppContainerIdentity.Resolve(daemonIsRootless, _options.ContainerIdentity);

        // One retry, and it replans EVERYTHING. Reassigning the one port that lost its race would leave every
        // sibling's environment pointing at the old number, and a host-visible URL baked into a created container
        // cannot be repaired by persisting what was observed.
        var attempt = 0;
        while (true)
        {
            await CreateInstanceNetworkAsync(runtime, instanceId, cancellationToken);

            using var hold = HoldPorts(manifest);
            var plan = BuildPlan(manifest, instanceId, variables, identity, hold, bridgeGrant);
            await PrepareStorageAsync(instanceId, manifest, cancellationToken);

            try
            {
                return await CreateAndStartAsync(runtime,
                        daemonIsRootless,
                        instanceId,
                        manifest,
                        plan,
                        hold,
                        commitBeforeStart,
                        cancellationToken);
            }
            catch (ExternalAppPipelineException failure)
                when (attempt == 0 && failure.Failure.Category == ExternalAppFailureCategory.PortUnavailable)
            {
                _logger.LogWarning("A host port for external application instance {InstanceId} was taken between the probe and the create; replanning the whole attempt once.",
                    instanceId);
                attempt++;

                // Required rather than best-effort: the replan creates the same containers again, and a survivor of
                // the first attempt would collide with them by name.
                await RequireTeardownAsync(runtime, instanceId, CancellationToken.None);
            }
        }
    }

    private async Task PullImagesAsync(IContainerRuntime runtime,
        Guid instanceId,
        ApplicationManifest manifest,
        CancellationToken cancellationToken)
    {
        foreach (var service in manifest.Services.DistinctBy(static service => service.Image, StringComparer.Ordinal))
        {
            await PullImageAsync(runtime, instanceId, service.Image, service.Name, cancellationToken);
        }
    }

    /// <summary>
    ///     One image, reported under <paramref name="progressLabel" />. Shared with the storage helper so its image
    ///     is acquired exactly the way an application's is: skipped when the digest is already local, reported while
    ///     it is not, and a failure that is an image-pull failure rather than something else.
    /// </summary>
    private async Task PullImageAsync(IContainerRuntime runtime,
        Guid instanceId,
        string image,
        string progressLabel,
        CancellationToken cancellationToken)
    {
        try
        {
            if (await runtime.ImageExistsAsync(image, cancellationToken))
            {
                // A digest that is already local is the same bytes by definition, so re-pulling it would buy
                // nothing and cost the whole image on every start.
                return;
            }

            // Fire-and-forget by contract: Progress<T> hands the report to a synchronous Action<T>.
            var progress = new Progress<ContainerPullProgress>(report => _ = PublishPullProgressAsync(instanceId, progressLabel, report));
            await runtime.PullImageAsync(image, progress, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not ExternalAppPipelineException)
        {
            throw Failed(ExternalAppFailurePhase.Pull, exception);
        }
    }

    private async Task CreateInstanceNetworkAsync(IContainerRuntime runtime, Guid instanceId, CancellationToken cancellationToken)
    {
        try
        {
            // Internal = false: V1 enforces no outbound restriction, and an internal network would be a confinement
            // claim the disclosure does not make.
            _ = await runtime.CreateNetworkAsync(new ContainerNetworkSpecification
                {
                    Name = ExternalAppLabels.NetworkName(instanceId),
                    Labels = ExternalAppLabels.For(_installId, instanceId),
                    Internal = false
                },
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Failed(ExternalAppFailurePhase.Network, exception);
        }
    }

    private static ExternalAppPortHold HoldPorts(ApplicationManifest manifest)
    {
        try
        {
            return ExternalAppPortAllocator.Hold(manifest);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Failed(ExternalAppFailurePhase.Create, exception);
        }
    }

    private DeploymentPlan BuildPlan(ApplicationManifest manifest,
        Guid instanceId,
        IReadOnlyDictionary<string, string> variables,
        ResolvedContainerIdentity identity,
        ExternalAppPortHold hold,
        ContainerBridgeGrant? bridgeGrant)
    {
        try
        {
            // Describe rather than Prepare: the plan runs before any byte is written, so a manifest whose mounts
            // collide is refused with the instance directory still untouched.
            return DeploymentPlanner.Plan(manifest, instanceId, _installId, variables, identity, hold.Ports, _layout.Describe(instanceId), bridgeGrant);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Failed(ExternalAppFailurePhase.Plan, exception);
        }
    }

    private async Task PrepareStorageAsync(Guid instanceId, ApplicationManifest manifest, CancellationToken cancellationToken)
    {
        try
        {
            _ = await _layout.PrepareAsync(instanceId, manifest, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Failed(ExternalAppFailurePhase.Storage, exception);
        }
    }

    private void VerifyBindSources(DeploymentPlan plan)
    {
        try
        {
            _layout.VerifyBindSources(plan);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Failed(ExternalAppFailurePhase.Storage, exception);
        }
    }

    private async Task<IReadOnlyList<ExternalAppPublishedPort>> CreateAndStartAsync(IContainerRuntime runtime,
        bool daemonIsRootless,
        Guid instanceId,
        ApplicationManifest manifest,
        DeploymentPlan plan,
        ExternalAppPortHold hold,
        Func<IReadOnlyList<ExternalAppPublishedPort>, CancellationToken, Task>? commitBeforeStart,
        CancellationToken cancellationToken)
    {
        var containerIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var instanceRoot = _layout.Describe(instanceId).InstanceRoot;

        foreach (var service in plan.Services)
        {
            // Immediately before the create, not once when the storage was prepared: every daemon call since then is
            // a window in which a path component could have been replaced by a link, and the plan carries nothing but
            // path strings, which resolve nothing.
            VerifyBindSources(plan);

            // The listener is released here and nowhere earlier: the window between letting go of a port and the
            // daemon taking it is the race, and it widens with every image pulled in between.
            hold.Release(service.ServiceName);

            string containerId;
            try
            {
                containerId = await runtime.RunContainerAsync(service.Specification, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw Failed(ExternalAppFailurePhase.Create, exception, HostPortsOf(service));
            }

            containerIds[service.ServiceName] = containerId;
            var inspection = await InspectAsync(runtime, containerId, cancellationToken);
            Verify(service, inspection, daemonIsRootless, afterStart: false, instanceRoot);
        }

        if (commitBeforeStart is not null)
        {
            // The PLANNED ports, because nothing has started yet. The final write replaces them with what the daemon
            // actually bound; this one exists so the committed row is never half-written.
            await commitBeforeStart(PlannedPortsOf(plan), cancellationToken);
        }

        return await StartAllAsync(runtime, daemonIsRootless, instanceId, manifest, plan, containerIds, cancellationToken);
    }

    /// <summary>
    ///     Step 12b: start each service in dependency order, verify what the daemon actually did, wait for the
    ///     condition anything asks of it, and prove once that the instance can write to its own storage. Shared with
    ///     the start path that REUSES containers, because a reused container has to clear exactly the same bar.
    /// </summary>
    private async Task<IReadOnlyList<ExternalAppPublishedPort>> StartAllAsync(IContainerRuntime runtime,
        bool daemonIsRootless,
        Guid instanceId,
        ApplicationManifest manifest,
        DeploymentPlan plan,
        IReadOnlyDictionary<string, string> containerIds,
        CancellationToken cancellationToken)
    {
        var published = new List<ExternalAppPublishedPort>();
        var instanceRoot = _layout.Describe(instanceId).InstanceRoot;
        var probed = false;

        foreach (var service in plan.Services)
        {
            var containerId = containerIds[service.ServiceName];
            try
            {
                await runtime.StartContainerAsync(containerId, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw Failed(ExternalAppFailurePhase.Start, exception, HostPortsOf(service));
            }

            var inspection = await InspectAsync(runtime, containerId, cancellationToken);
            Verify(service, inspection, daemonIsRootless, afterStart: true, instanceRoot);

            // The post-start read-back IS the binding read-back: what the daemon actually bound is the same evidence
            // the port rule is verified against, and asking twice would let the two answers differ.
            published.AddRange(inspection.PublishedPorts.Select(port =>
                new ExternalAppPublishedPort(service.ServiceName, port.ContainerPort, port.HostPort)));

            await WaitUntilReadyAsync(runtime, service, containerId, plan, cancellationToken);

            if (!probed)
            {
                probed = await ProbeStorageAsync(runtime, manifest, service.ServiceName, containerId, daemonIsRootless, cancellationToken);
            }
        }

        return published;
    }

    private static async Task<ContainerInspection> InspectAsync(IContainerRuntime runtime,
        string containerId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await runtime.InspectAsync(containerId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Failed(ExternalAppFailurePhase.Verify, exception);
        }
    }

    private void Verify(ServiceDeployment service,
        ContainerInspection inspection,
        bool daemonIsRootless,
        bool afterStart,
        string instanceRoot)
    {
        var violations = ApplicationContainerPolicy.FindViolations(service.Specification, inspection, daemonIsRootless, afterStart, instanceRoot);
        if (violations.Count > 0)
        {
            // The failure this raises counts them and stops there — a violation can name a host path, and the stored
            // summary is rendered in a browser. The node log is where the operator reads which checks failed.
            _logger.LogWarning("Service {Service} failed policy verification ({Phase}): {Violations}.",
                service.ServiceName,
                afterStart ? "after start" : "before start",
                string.Join("; ", violations));

            throw new ExternalAppPipelineException(ExternalAppFailureTranslator.ForViolations(service.ServiceName, violations));
        }
    }

    /// <summary>
    ///     Waits until the service satisfies the strongest condition anything asks of it: running always, and healthy
    ///     when it declares a healthcheck or when a dependant names that condition.
    /// </summary>
    private async Task WaitUntilReadyAsync(IContainerRuntime runtime,
        ServiceDeployment service,
        string containerId,
        DeploymentPlan plan,
        CancellationToken cancellationToken)
    {
        var mustBeHealthy = service.Specification.Healthcheck is not null
                            || plan.Services.Any(candidate => candidate.DependsOn.Any(dependency =>
                                dependency.RequiresHealthy
                                && string.Equals(dependency.Service, service.ServiceName, StringComparison.Ordinal)));

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(_options.ServiceReadyTimeoutSeconds), _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var warned = false;

        while (true)
        {
            ContainerInspection inspection;
            try
            {
                inspection = await runtime.InspectAsync(containerId, linked.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw NotReady(service.ServiceName, "did not become ready in time");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw Failed(ExternalAppFailurePhase.Wait, exception);
            }

            if (IsFinished(inspection.State))
            {
                // A container that exited is not "not ready yet": waiting out the whole timeout on it would report a
                // crash loop as a slow start.
                throw NotReady(service.ServiceName, "exited while it was being waited for");
            }

            if (inspection.State.Running)
            {
                if (!mustBeHealthy)
                {
                    return;
                }

                switch (inspection.State.Health)
                {
                    case ContainerHealthState.Healthy:
                        return;
                    case ContainerHealthState.Unhealthy:
                        throw NotReady(service.ServiceName, "reported itself unhealthy");
                    case ContainerHealthState.None when !warned:
                        warned = true;
                        _logger.LogWarning("Service '{Service}' declares a healthcheck but the daemon reports no health state for it; waiting for the deadline.",
                            service.ServiceName);
                        break;
                    default:
                        break;
                }
            }

            try
            {
                await Task.Delay(ReadyPollInterval, _timeProvider, linked.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw NotReady(service.ServiceName, "did not become ready in time");
            }
        }
    }

    private static bool IsFinished(ContainerRunState state)
    {
        return !state.Running
               && (string.Equals(state.Status, "exited", StringComparison.Ordinal)
                   || string.Equals(state.Status, "dead", StringComparison.Ordinal));
    }

    /// <summary>
    ///     Proves the uid mapping between this container and the engine-created bind mount, once per attempt, on the
    ///     first started service that has one. Returns whether a probe actually ran.
    /// </summary>
    private static async Task<bool> ProbeStorageAsync(IContainerRuntime runtime,
        ApplicationManifest manifest,
        string serviceName,
        string containerId,
        bool daemonIsRootless,
        CancellationToken cancellationToken)
    {
        var declared = manifest.Services.FirstOrDefault(service => string.Equals(service.Name, serviceName, StringComparison.Ordinal));
        if (declared is null || declared.Storage.Count == 0)
        {
            return false;
        }

        var storage = declared.Storage[0];

        bool writable;
        try
        {
            writable = await runtime.ProbeWritablePathAsync(containerId, storage.ContainerPath, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new ExternalAppPipelineException(UnwritableStorage(daemonIsRootless), exception);
        }

        return writable ? true : throw new ExternalAppPipelineException(UnwritableStorage(daemonIsRootless));
    }

    private static ExternalAppFailure UnwritableStorage(bool daemonIsRootless)
    {
        // The daemon mode is named because it is the whole diagnosis: in-container root maps to the invoking user
        // under a rootless daemon and does not under a rootful one, and the fix differs completely between them.
        return new ExternalAppFailure(ExternalAppFailureCategory.StorageError,
            $"This application cannot write to its own storage directory under a {(daemonIsRootless ? "rootless" : "rootful")} container daemon.");
    }

    private static ExternalAppPipelineException NotReady(string serviceName, string what)
    {
        return new ExternalAppPipelineException(new ExternalAppFailure(ExternalAppFailureCategory.HealthCheckFailed,
            $"Service '{serviceName}' {what}."));
    }

    private static ExternalAppPipelineException Failed(ExternalAppFailurePhase phase,
        Exception exception,
        IReadOnlyList<int>? plannedHostPorts = null)
    {
        return new ExternalAppPipelineException(ExternalAppFailureTranslator.Translate(phase, exception, plannedHostPorts), exception);
    }

    /// <summary>Every publication of a plan, as the row records them.</summary>
    private static IReadOnlyList<ExternalAppPublishedPort> PlannedPortsOf(DeploymentPlan plan)
    {
        return
        [
            .. plan.Services.SelectMany(service => service.Specification.PublishedPorts
                                                          .Where(static publication => publication.HostPort.HasValue)
                                                          .Select(publication => new ExternalAppPublishedPort(service.ServiceName,
                                                              publication.ContainerPort,
                                                              publication.HostPort!.Value)))
        ];
    }

    /// <summary>
    ///     The host ports of the ONE service that failed. Passing the whole attempt's ports would misdiagnose every
    ///     failure as a lost port race: the siblings' listeners are still held by this process, so they read as
    ///     unbindable whatever went wrong.
    /// </summary>
    private static IReadOnlyList<int> HostPortsOf(ServiceDeployment service)
    {
        return
        [
            .. service.Specification.PublishedPorts
                      .Where(static publication => publication.HostPort.HasValue)
                      .Select(static publication => publication.HostPort!.Value)
        ];
    }

    /// <summary>
    ///     Removes every container and network carrying this instance's three labels, and REPORTS whether the
    ///     containers are actually gone. BY LABEL, never by the name <c>xe-app-&lt;id&gt;</c>: a second installation
    ///     pointed at the same daemon generates the same names.
    ///     <para>
    ///         It still never throws — a teardown that did would strand the row in a transient status — but the
    ///         answer is no longer silence. Every caller that goes on to DESTROY state (the row, the storage) or to
    ///         materialise replacements has to know that the old containers are confirmed absent rather than
    ///         merely asked to leave, and the confirmation is a second listing after the removals.
    ///     </para>
    /// </summary>
    /// <returns><see langword="true" /> when no container of this instance is left on the daemon.</returns>
    internal async Task<bool> RemoveInstanceContainersAsync(IContainerRuntime runtime, Guid instanceId, CancellationToken cancellationToken)
    {
        var labels = ExternalAppLabels.For(_installId, instanceId);

        try
        {
            foreach (var containerId in await runtime.ListContainersAsync(labels, cancellationToken))
            {
                await runtime.RemoveContainerAsync(containerId, cancellationToken);
            }

            foreach (var networkId in await runtime.ListNetworksAsync(labels, cancellationToken))
            {
                await runtime.RemoveNetworkAsync(networkId, cancellationToken);
            }

            // The daemon's own answer, not the absence of an exception: a removal that was accepted and then failed,
            // or a container created under this instance's labels while the teardown ran, is visible only here.
            var left = await runtime.ListContainersDetailedAsync(labels, cancellationToken);
            if (left.Count == 0)
            {
                return true;
            }

            _logger.LogWarning("{Count} container(s) of external application instance {InstanceId} are still on the container runtime after its teardown.",
                left.Count,
                instanceId);

            return false;
        }
#pragma warning disable CA1031 // A teardown is best-effort by contract; its failure must not replace the failure that caused it.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            _logger.LogWarning(exception,
                "Removing the containers and networks of external application instance {InstanceId} did not complete; the next teardown lists what is left.",
                instanceId);

            return false;
        }
    }

    /// <summary>
    ///     The teardown as a PRECONDITION: the same removal, and a failure rather than a warning when the containers
    ///     are still there. Every caller that would go on to delete the row, wipe the storage or create replacements
    ///     runs this one, because each of those destroys the evidence the next attempt needs.
    /// </summary>
    private async Task RequireTeardownAsync(IContainerRuntime runtime, Guid instanceId, CancellationToken cancellationToken)
    {
        if (await RemoveInstanceContainersAsync(runtime, instanceId, cancellationToken))
        {
            return;
        }

        // Content-free on purpose: what is left on the daemon is named in the log, and a summary is a string the
        // browser renders.
        throw new ExternalAppPipelineException(new ExternalAppFailure(ExternalAppFailureCategory.Unknown,
            "This application's existing containers could not be removed, so nothing was changed; try again."));
    }

    /// <summary>
    ///     Deletes the CONTENTS of the instance's volumes directory from inside a short-lived engine-owned container,
    ///     leaving the directory itself for the host-side delete that follows.
    ///     <para>
    ///         The engine cannot always do this itself, and the reason is the uid mapping rather than a bug: an
    ///         application whose image runs as its own non-root user creates <c>0700</c> directories owned by a host
    ///         uid inside the operator's subuid range under a rootless daemon, and by that uid outright under a
    ///         rootful one. Neither is the engine's, so it can traverse nothing and unlink nothing — which is why a
    ///         reset reported a storage failure and an uninstall silently left the instance directory on disk after
    ///         promising to delete it. The install-time write probe never saw it, because that probe writes as the
    ///         container's INITIAL user, which is root.
    ///     </para>
    ///     <para>
    ///         In-container root is the identity that can: under a rootless daemon it is the engine's own uid holding
    ///         <c>CAP_DAC_OVERRIDE</c> across the whole mapped range inside the user namespace, and under a rootful
    ///         one it is host root. What keeps that from being a hole is the confinement, not the identity — one
    ///         bind mount, re-validated for links immediately before the run and read back from the daemon after it,
    ///         no network, no added capabilities, no ports, a pinned digest and a fixed command into which no
    ///         engine-side value is interpolated.
    ///     </para>
    /// </summary>
    internal async Task WipeVolumeContentsAsync(IContainerRuntime runtime, Guid instanceId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        var volumesRoot = FindVolumeContents(instanceId);
        if (volumesRoot is null)
        {
            // Nothing to wipe: an application with no storage[], or a directory the previous attempt emptied. No
            // image is pulled and no container is created for it.
            return;
        }

        await PullImageAsync(runtime, instanceId, _options.StorageHelperImage, ExternalAppLabels.StorageWipeValue, cancellationToken);

        string containerId;
        try
        {
            containerId = await runtime.RunContainerAsync(BuildStorageHelper(instanceId, volumesRoot), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not ExternalAppPipelineException)
        {
            throw Failed(ExternalAppFailurePhase.Storage, exception);
        }

        try
        {
            VerifyHelperMount(await InspectAsync(runtime, containerId, cancellationToken), volumesRoot);
            await RunHelperToCompletionAsync(runtime, containerId, cancellationToken);

            if (FindVolumeContents(instanceId) is not null)
            {
                // The helper exited 0 and something is still there. Reporting success would let the uninstall go on
                // to delete the row while the data it promised to remove is on disk.
                throw Failed(ExternalAppFailurePhase.Storage,
                    new ExternalAppStorageException("The storage helper exited successfully but the instance's volumes directory is not empty."));
            }
        }
        finally
        {
            await RemoveHelperAsync(runtime, containerId, instanceId);
        }
    }

    /// <summary>The layout's answer, with its refusal turned into the same storage failure every other phase reports.</summary>
    private string? FindVolumeContents(Guid instanceId)
    {
        try
        {
            return _layout.FindVolumeContents(instanceId);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Failed(ExternalAppFailurePhase.Storage, exception);
        }
    }

    /// <summary>
    ///     Everything the storage helper is. Every field is set explicitly, including the ones whose default would
    ///     be right: this container runs as root over an application's data, so "we did not set it" is not evidence.
    /// </summary>
    private ContainerSpecification BuildStorageHelper(Guid instanceId, string volumesRoot)
    {
        return new ContainerSpecification
        {
            Image = _options.StorageHelperImage,
            Name = ExternalAppLabels.StorageHelperContainerName(instanceId),

            // No user, and deliberately not one taken from the instance: in-container root is the ONLY identity that
            // can unlink what an application's own non-root user left behind, and a --user would reintroduce the
            // defect this container exists to fix.
            User = null,
            Labels = ExternalAppLabels.ForStorageHelper(_installId, instanceId),
            Environment = new Dictionary<string, string>(StringComparer.Ordinal),

            // The ONE mount, and the whole confinement: the helper can reach nothing outside this instance's
            // volumes directory whatever its command does. Read-back below proves the daemon applied exactly it.
            Mounts =
            [
                new ContainerMount
                {
                    HostPath = volumesRoot,
                    ContainerPath = HelperMountPath,
                    ReadOnly = false
                }
            ],
            PublishedPorts = [],

            // Docker's own default set, kept and not widened. CAP_DAC_OVERRIDE is in it and is the point; dropping
            // ALL would leave in-container root unable to traverse the 0700 directories it is here to remove, and
            // adding anything back would exceed the default set the catalog rule bounds every container by.
            CapabilitiesToDrop = [],
            CapabilitiesToAdd = [],
            SecurityOptions = [ApplicationContainerPolicy.NoNewPrivileges, DockerSeccompProfile.SecurityOption],

            // Nothing is written outside the mount, so the root filesystem is read-only.
            ReadOnlyRootFilesystem = true,

            // Never the instance network: an uninstall has already removed it by this point, and a container that
            // only deletes files on a local bind mount has no use for one.
            NetworkName = IsolatedNetworkName,
            NetworkAliases = [],
            RestartMode = ContainerRestartMode.None,
            MemoryBytes = 0,
            NanoCpus = 0,
            PidsLimit = StorageHelperPidsLimit,
            Entrypoint = null,
            Command = ["sh", "-c", StorageHelperCommand],
            ExtraHosts = [],
            Healthcheck = null
        };
    }

    /// <summary>
    ///     What the daemon says it mounted, before the helper is started. The specification says what was asked for;
    ///     only this says what a container about to run as root is actually able to reach.
    /// </summary>
    private static void VerifyHelperMount(ContainerInspection inspection, string volumesRoot)
    {
        if (inspection.Mounts.Count != 1)
        {
            throw Failed(ExternalAppFailurePhase.Storage,
                new ExternalAppStorageException("The storage helper was created with more than the one mount it was asked for."));
        }

        var mount = inspection.Mounts[0];
        if (!string.Equals(mount.Source, volumesRoot, StringComparison.Ordinal)
            || !string.Equals(mount.Destination, HelperMountPath, StringComparison.Ordinal)
            || mount.ReadOnly)
        {
            throw Failed(ExternalAppFailurePhase.Storage,
                new ExternalAppStorageException("The storage helper's mount is not the writable bind of this instance's volumes directory."));
        }
    }

    /// <summary>
    ///     Starts the helper and waits for it to finish, on the injected clock. A non-zero exit or a deadline is a
    ///     storage failure: the wipe either completed or it did not, and there is no partial success to report.
    /// </summary>
    private async Task RunHelperToCompletionAsync(IContainerRuntime runtime, string containerId, CancellationToken cancellationToken)
    {
        try
        {
            await runtime.StartContainerAsync(containerId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Failed(ExternalAppFailurePhase.Storage, exception);
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(_options.ServiceReadyTimeoutSeconds), _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);

        while (true)
        {
            ContainerInspection inspection;
            try
            {
                inspection = await runtime.InspectAsync(containerId, linked.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw HelperFailed("did not finish in time");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw Failed(ExternalAppFailurePhase.Storage, exception);
            }

            if (IsFinished(inspection.State))
            {
                if (inspection.State.ExitCode != 0)
                {
                    throw HelperFailed("could not remove this application's data");
                }

                return;
            }

            try
            {
                await Task.Delay(ReadyPollInterval, _timeProvider, linked.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw HelperFailed("did not finish in time");
            }
        }
    }

    private static ExternalAppPipelineException HelperFailed(string what)
    {
        // Through the translator, so the summary a browser renders is the same content-free storage sentence every
        // other storage failure carries; the detail is the inner exception, which only the node log sees.
        return Failed(ExternalAppFailurePhase.Storage, new ExternalAppStorageException($"The storage helper {what}."));
    }

    /// <summary>
    ///     Removes the helper in a <c>finally</c>, by id. Best-effort, because a helper left behind is not a reason
    ///     to replace the verdict of the operation that ran it: it carries the instance's labels, so the next
    ///     teardown and the boot pass's orphan sweep both remove it anyway.
    /// </summary>
    private async Task RemoveHelperAsync(IContainerRuntime runtime, string containerId, Guid instanceId)
    {
        try
        {
            await runtime.RemoveContainerAsync(containerId, CancellationToken.None);
        }
#pragma warning disable CA1031 // As above: a cleanup failure must not replace the failure that caused it.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            _logger.LogWarning(exception,
                "The storage helper container of external application instance {InstanceId} could not be removed; the next teardown removes it by label.",
                instanceId);
        }
    }

    /// <summary>
    ///     Deletes the instance directory and reports whether it is gone, turning the layout's link REFUSAL into the
    ///     same false the caller already handles. Shared with the boot reconciler, which completes the same uninstall.
    /// </summary>
    internal bool DeleteStorage(Guid instanceId)
    {
        try
        {
            return _layout.Delete(instanceId);
        }
        catch (ExternalAppStorageException exception)
        {
            _logger.LogWarning(exception,
                "The data directory of external application instance {InstanceId} was refused rather than deleted; it can be inspected by hand.",
                instanceId);

            return false;
        }
    }

    /// <summary>A status compare-and-swap addressed at what the cursor believes the row is.</summary>
    private ExternalAppStatusUpdate Transition(InstanceCursor cursor,
        ExternalAppInstanceStatus newStatus,
        ExternalAppInstanceEventKind kind)
    {
        return new ExternalAppStatusUpdate(cursor.InstanceId,
            cursor.Version,
            new HashSet<ExternalAppInstanceStatus>
            {
                cursor.Status
            },
            newStatus,
            kind,
            EventDetailJson: null,
            Now());
    }

    /// <summary>
    ///     Applies one transition, advances the cursor and publishes the ping. A lost swap returns
    ///     <see langword="false" /> WITHOUT advancing: inside a pipeline the other writer's verdict is the newer one.
    /// </summary>
    private async Task<bool> ApplyAsync(IExternalAppInstanceStore store,
        InstanceCursor cursor,
        ExternalAppStatusUpdate update,
        CancellationToken cancellationToken)
    {
        var result = await store.UpdateStatusAsync(update, cancellationToken);
        if (!result.Applied)
        {
            _logger.LogWarning("A {Status} transition for external application instance {InstanceId} lost its compare-and-swap; another writer moved the row.",
                update.NewStatus,
                cursor.InstanceId);
            return false;
        }

        cursor.Version = result.Version;
        cursor.Status = update.NewStatus;
        cursor.Sequence = result.Sequence;
        await PublishAsync(cursor.InstanceId, result.Sequence, update.EventKind, update.NewStatus);
        return true;
    }

    /// <summary>
    ///     Settles a failed attempt: containers and network removed by label, storage KEPT, the row compare-and-swapped
    ///     to <c>Failed</c> with the translated category.
    /// </summary>
    /// <remarks>
    ///     Deleting the instance directory here would be a data-destroying act on a path a retry can recover from.
    ///     Only reset and uninstall delete anything.
    /// </remarks>
    private async Task FailAsync(IExternalAppInstanceStore store,
        IContainerRuntime? runtime,
        InstanceCursor cursor,
        ExternalAppFailure failure)
    {
        if (runtime is not null)
        {
            // Best-effort here and nowhere else in this file: this settles a row that is already failing, and nothing
            // downstream of it destroys state or creates a replacement.
            _ = await RemoveInstanceContainersAsync(runtime, cursor.InstanceId, CancellationToken.None);
        }

        var summary = failure.Summary.Length > MaxFailureSummaryLength
            ? failure.Summary[..MaxFailureSummaryLength]
            : failure.Summary;

        // The one place every failed operation settles, so the one place the node log records that it did. The stored
        // summary is content-free by contract, which is exactly why it is safe to log verbatim.
        _logger.LogWarning("The operation on external application instance {InstanceId} failed ({Category}): {Summary}",
            cursor.InstanceId,
            failure.Category,
            summary);

        // CancellationToken.None: this runs BECAUSE the operation was cancelled or failed, and a settling write that
        // honoured the dead token would leave the row transient forever.
        if (await ApplyAsync(store, cursor, FailureTransition(cursor, failure.Category, summary), CancellationToken.None))
        {
            return;
        }

        // The swap lost, which usually means another writer already settled the row — but it can also mean a writer
        // moved it WITHOUT settling it, and then nobody has written the failure. A pipeline owes its row a terminal
        // status, so re-read once and try again while the row is still in a transient this operation owns.
        var current = await store.GetAsync(cursor.InstanceId, CancellationToken.None);
        if (current is null || Array.IndexOf(Transient, current.Status) < 0)
        {
            return;
        }

        cursor.Version = current.Version;
        cursor.Status = current.Status;
        _ = await ApplyAsync(store, cursor, FailureTransition(cursor, failure.Category, summary), CancellationToken.None);
    }

    private ExternalAppStatusUpdate FailureTransition(InstanceCursor cursor, ExternalAppFailureCategory category, string summary)
    {
        return Transition(cursor, ExternalAppInstanceStatus.Failed, ExternalAppInstanceEventKind.Failed) with
        {
            FailureCategory = category,
            FailureSummary = summary
        };
    }

    /// <summary>
    ///     The shutdown path: no container is touched and the row keeps its transient status on purpose. Flattening
    ///     it would make an interrupted uninstall unresumable, and stopping the containers would take the user's
    ///     applications down with the engine.
    /// </summary>
    private void LeaveTransientForShutdown(Guid instanceId)
    {
        _logger.LogInformation("The operation on external application instance {InstanceId} was interrupted by shutdown; its row is left as it stands for the next boot to settle.",
            instanceId);
    }

    private async Task PublishAsync(Guid instanceId,
        long sequence,
        ExternalAppInstanceEventKind kind,
        ExternalAppInstanceStatus status)
    {
        try
        {
            await _publisher.PublishAsync(instanceId, sequence, kind, status, CancellationToken.None);
        }
#pragma warning disable CA1031 // A subscriber that cannot be reached must not fail the operation whose result it describes.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            _logger.LogDebug(exception, "Publishing the {Kind} event for external application instance {InstanceId} failed.", kind, instanceId);
        }
    }

    private async Task PublishPullProgressAsync(Guid instanceId, string service, ContainerPullProgress report)
    {
        try
        {
            await _publisher.PublishPullProgressAsync(instanceId,
                                service,
                                report.LayerCount,
                                report.CompletedLayers,
                                report.CurrentBytes,
                                CancellationToken.None);
        }
#pragma warning disable CA1031 // Progress is worthless after the fact; a dropped report degrades to a stale bar.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            _logger.LogDebug(exception, "Publishing pull progress for external application instance {InstanceId} failed.", instanceId);
        }
    }
}
