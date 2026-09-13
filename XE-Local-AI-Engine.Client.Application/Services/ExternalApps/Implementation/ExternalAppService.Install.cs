namespace XE_Local_AI_Engine.Client.Services.ExternalApps.Implementation;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Containers;
using XE_Local_AI_Engine.Client.Services.Containers.Bridge;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog;

/// <summary>
///     Install: the seven admission steps that run on the caller's thread and the seven pipeline steps that run on
///     the operation runner. The split is the contract — a caller holds the instance id and its version before the
///     first image layer is pulled, and a browser that disconnects does not abort the install it started.
/// </summary>
internal sealed partial class ExternalAppService
{
    private const string GpuRequired = "required";
    private const int MaxDisplayNameLength = 128;

    /// <summary>
    ///     Admission's own wording for a bridge this node did not open, naming the same setting the planner's refusal
    ///     names — the planner can add which service and which token failed, which admission has not looked at and must
    ///     not invent. One copy, because install, update and the Start/Restart admission hand it to the same operator:
    ///     a second copy would drift from this one without a gate noticing.
    /// </summary>
    private const string BridgeUnavailableDetail =
        "This application reads the node's container bridge, and this node did not open one. Turn it on with "
        + $"'{ContainerBridgeOptions.SectionName}:{nameof(ContainerBridgeOptions.Enabled)}' and an IPv4 host interface it can bind.";

    public async Task<InstallPreview> PreviewInstallAsync(string applicationId, CancellationToken cancellationToken = default)
    {
        EnsureEnabled();

        await using var scope = _scopeFactory.CreateAsyncScope();
        var services = ScopedServices.From(scope.ServiceProvider);
        var manifest = await RequireApplicationAsync(services.Catalog, applicationId, cancellationToken).ConfigureAwait(false);
        var admission = await EvaluateInstallAsync(services, manifest, cancellationToken).ConfigureAwait(false);

        return new InstallPreview(manifest.Id,
            manifest.ManifestVersion,
            manifest.ManifestSha256,
            admission.BlockedReason is null,
            admission.BlockedReason,
            admission.ExistingInstanceId,
            manifest.Permissions,
            ExternalAppEffectivePermissions.From(manifest),
            // Every DECLARED variable, not the required ones: the install form renders the optional rows too, and an
            // application with sixteen optional settings would otherwise arrive with none of them.
            manifest.Variables,
            admission.Resources,
            admission.Runtime,
            admission.MissingCapabilities);
    }

    public async Task<ExternalAppInstanceSummary> InstallAsync(InstallCommand command, CancellationToken cancellationToken = default)
    {
        EnsureEnabled();
        ArgumentNullException.ThrowIfNull(command);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var services = ScopedServices.From(scope.ServiceProvider);

        // 1–4: the catalog, the runtime, what the manifest requires of it, and this machine's resources.
        var manifest = await RequireApplicationAsync(services.Catalog, command.ApplicationId, cancellationToken).ConfigureAwait(false);
        var admission = await EvaluateInstallAsync(services, manifest, cancellationToken).ConfigureAwait(false);
        if (admission.BlockedReason is { } blocked && blocked != ExternalAppBlockedReason.AlreadyInstalled)
        {
            // AlreadyInstalled is deliberately NOT refused here: the authoritative check is the one inside the
            // application lock below, and answering from this unsynchronised read would be a race with a nicer
            // message.
            throw Refuse(blocked, admission);
        }

        // 5: the variables, named but never echoed.
        var variables = ValidateVariables(manifest, command.Variables);

        // 6: acceptance is bound to the manifest that was actually disclosed, and is never inferred.
        RequireFingerprint(manifest, command.ManifestVersion, command.ManifestSha256);
        var granted = ExternalAppEffectivePermissions.Diff(NoPermissions, ExternalAppEffectivePermissions.From(manifest));
        if (!command.AcceptPermissions)
        {
            throw new ExternalAppPermissionChangeRequiresAcknowledgementException("This application's declared permissions have to be accepted before it can be installed.",
                granted);
        }

        // 7: the instance gate FIRST, so a reconcile or an observer tick skips this row for as long as the install
        // runs; the application lock wraps only the one-per-application read and the insert it authorises.
        var instanceId = Guid.NewGuid();
        IDisposable? lease = null;
        try
        {
            lease = await _gate.TryEnterAsync(ExternalAppInstanceGate.InstanceKey(instanceId)).ConfigureAwait(false)
                    ?? throw new ExternalAppOperationInFlightException("An operation is already running on this instance.");

            var createdAtUtc = Now();
            var version = await InsertInstanceAsync(services.Store,
                    command,
                    manifest,
                    granted,
                    variables,
                    admission.Runtime.Provider,
                    instanceId,
                    createdAtUtc,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!_runner.TryStart(instanceId,
                    ExternalAppOperationKind.Install,
                    lease,
                    (provider, token) => RunInstallAsync(provider, instanceId, manifest, variables, token),
                    out _))
            {
                throw new ExternalAppOperationInFlightException("An operation is already running on this instance.");
            }

            // Ownership of the lease is now the runner's, released in its finally. Cleared so the finally below
            // cannot release a gate the background operation is still standing behind.
            lease = null;

            // The admitted snapshot, built from what was just written rather than re-read: a read would race the
            // pipeline's own first transition and could hand back a status the caller never admitted.
            return new ExternalAppInstanceSummary(instanceId,
                manifest.Id,
                DisplayNameFor(command, manifest),
                manifest.ManifestVersion,
                ExternalAppInstanceStatus.Installing,
                ExternalAppDesiredState.Stopped,
                FailureCategory: null,
                FailureSummary: null,
                UpdateAvailable: false,
                AvailableManifestVersion: null,
                CatalogMissing: false,
                createdAtUtc,
                version);
        }
        finally
        {
            if (lease is not null)
            {
                // The lease is still ours, so admission was REFUSED: the runner never took it. The gate minted a map
                // entry for this freshly generated id, and no instance will ever carry it, so the entry is dropped
                // from inside the critical section rather than left behind on every rejected install.
                _gate.Forget(ExternalAppInstanceGate.InstanceKey(instanceId));
            }

            lease?.Dispose();
        }
    }

    private async Task<long> InsertInstanceAsync(IExternalAppInstanceStore store,
        InstallCommand command,
        ApplicationManifest manifest,
        IReadOnlyList<string> granted,
        IReadOnlyDictionary<string, string> variables,
        string runtimeProvider,
        Guid instanceId,
        long createdAtUtc,
        CancellationToken cancellationToken)
    {
        using var applicationLease = await _gate.TryEnterAsync(ExternalAppInstanceGate.ApplicationKey(command.ApplicationId)).ConfigureAwait(false)
                                     ?? throw new ExternalAppOperationInFlightException("Another install of this application is already being admitted.");

        var existing = await store.ListByApplicationAsync(command.ApplicationId, cancellationToken).ConfigureAwait(false);
        if (existing.Count > 0)
        {
            throw new ExternalAppAlreadyInstalledException($"The application '{command.ApplicationId}' is already installed; this version supports one instance of each.");
        }

        var create = new ExternalAppInstanceCreate(instanceId,
            manifest.Id,
            manifest.ManifestVersion,
            SerializeManifest(manifest),
            DisplayNameFor(command, manifest),
            SerializeVariables(variables),
            _layout.Describe(instanceId).InstanceRoot,
            runtimeProvider,
            RuntimeOverride: null,
            createdAtUtc,
            ExternalAppInstanceEventKind.PermissionAccepted,
            // Permission NAMES from the fixed vocabulary. A value has no business on an audit row that is replayed
            // into a browser.
            JsonSerializer.Serialize(new
            {
                permissions = granted
            }, ExternalAppJson.Options),
            // The instance's container-bridge credential, minted here and only here. It is sealed at rest by the same
            // interceptor that seals the variables, and its revocation is the row's deletion — there is no separate
            // revoke step, because a token whose instance no longer exists names nothing the verifier can find.
            ContainerBridgeToken.Mint(instanceId));

        var written = await store.CreateAsync(create, cancellationToken).ConfigureAwait(false);
        if (!written.Applied)
        {
            throw new ExternalAppConcurrencyException("The instance row could not be created; another writer reached it first.");
        }

        await PublishAsync(instanceId,
                written.Sequence,
                ExternalAppInstanceEventKind.PermissionAccepted,
                ExternalAppInstanceStatus.Installing)
            .ConfigureAwait(false);

        return written.Version;
    }

    /// <summary>Steps 8–14: the rebuild block, then the two writes that make the row <c>Running</c>.</summary>
    private async Task RunInstallAsync(IServiceProvider provider,
        Guid instanceId,
        ApplicationManifest manifest,
        IReadOnlyDictionary<string, string> variables,
        CancellationToken cancellationToken)
    {
        var services = ScopedServices.From(provider);
        var cursor = new InstanceCursor(instanceId, version: 0, ExternalAppInstanceStatus.Installing);
        IContainerRuntime? runtime = null;

        try
        {
            var resolution = await services.Resolver.ResolveAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            runtime = await services.Resolver.CreateRuntimeAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

            var published = await RebuildAsync(runtime,
                    resolution.Daemon.IsRootless,
                    instanceId,
                    manifest,
                    variables,
                    // Read back rather than carried from admission: the mint happens
                    // inside the row write, which is the only place it is durable.
                    await ResolveBridgeGrantAsync(instanceId, cancellationToken).ConfigureAwait(false),
                    commitBeforeStart: null,
                    cancellationToken)
                .ConfigureAwait(false);

            // Two events and two sequences for one status: "this application is installed" and "it is running" are
            // different facts, and a history that merged them could not say which of the two a later failure undid.
            var installed = Transition(cursor, ExternalAppInstanceStatus.Running, ExternalAppInstanceEventKind.Installed) with
            {
                DesiredState = ExternalAppDesiredState.Running,
                PublishedPortsJson = ExternalAppPublishedPorts.Serialize(published),
                StartedAtUtc = Now(),
                NeedsRecreate = false,
                ClearFailure = true
            };

            if (await ApplyAsync(services.Store, cursor, installed, cancellationToken).ConfigureAwait(false))
            {
                _ = await ApplyAsync(services.Store,
                        cursor,
                        Transition(cursor, ExternalAppInstanceStatus.Running, ExternalAppInstanceEventKind.Started),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (ExternalAppPipelineException failure)
        {
            await FailAsync(services.Store, runtime, cursor, failure.Failure).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_runner.IsShuttingDown)
        {
            LeaveTransientForShutdown(instanceId);
        }
        catch (OperationCanceledException)
        {
            await FailAsync(services.Store, runtime, cursor, Cancelled()).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // A pipeline settles its own row; letting anything escape would strand the instance transient.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            await FailAsync(services.Store,
                    runtime,
                    cursor,
                    ExternalAppFailureTranslator.Translate(ExternalAppFailurePhase.Resolution, exception))
                .ConfigureAwait(false);
        }
        finally
        {
            if (runtime is not null)
            {
                await runtime.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    ///     The four inputs an install and its preview both read, evaluated once. The preview reports them; the
    ///     command refuses on them.
    /// </summary>
    private async Task<InstallAdmission> EvaluateInstallAsync(ScopedServices services,
        ApplicationManifest manifest,
        CancellationToken cancellationToken)
    {
        var existing = await services.Store.ListByApplicationAsync(manifest.Id, cancellationToken).ConfigureAwait(false);
        var resolution = await services.Resolver.ResolveAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var missing = resolution.Capabilities.FindMissing(manifest.Requires);
        var resources = await _resourceGate.EvaluateAsync(manifest, _layout.Root, cancellationToken).ConfigureAwait(false);

        return new InstallAdmission(BlockingReason(existing.Count > 0, BridgeUnavailableFor(manifest), resolution, manifest, missing, resources),
            resolution,
            missing,
            resources,
            existing.Count > 0 ? existing[0].Id : null);
    }

    private static ExternalAppBlockedReason? BlockingReason(bool alreadyInstalled,
        bool bridgeUnavailable,
        ContainerRuntimeResolution resolution,
        ApplicationManifest manifest,
        IReadOnlyList<string> missingCapabilities,
        ExternalAppResourceVerdict resources)
    {
        // Already-installed comes first because it is the one reason no change to the machine can clear: the page
        // offers "Open", not "Install", and reporting a memory shortage beside it would be advice about a button
        // that is not there.
        if (alreadyInstalled)
        {
            return ExternalAppBlockedReason.AlreadyInstalled;
        }

        if (!resolution.Ready)
        {
            return ExternalAppBlockedReason.RuntimeUnavailable;
        }

        if (string.Equals(manifest.Permissions.Gpu, GpuRequired, StringComparison.Ordinal))
        {
            return ExternalAppBlockedReason.GpuNotSupported;
        }

        if (missingCapabilities.Count > 0)
        {
            return ExternalAppBlockedReason.RuntimeIncompatible;
        }

        // Ahead of the resource verdict, and for the same reason already-installed leads: no amount of free memory
        // clears it. A manifest that reads a bridge built-in cannot be planned on a node that opened no bridge, so
        // reporting a shortage beside it would be advice about the wrong thing.
        if (bridgeUnavailable)
        {
            return ExternalAppBlockedReason.BridgeUnavailable;
        }

        if (resources.Satisfied)
        {
            return null;
        }

        return resources.FailureCategory == ExternalAppFailureCategory.InsufficientDisk
            ? ExternalAppBlockedReason.InsufficientDisk
            : ExternalAppBlockedReason.InsufficientMemory;
    }

    private static ExternalAppValidationException Refuse(ExternalAppBlockedReason reason, InstallAdmission admission)
    {
        var detail = reason switch
        {
            ExternalAppBlockedReason.GpuNotSupported => ExternalAppFailureTranslator.ForGpuRequired().Summary,
            ExternalAppBlockedReason.RuntimeIncompatible =>
                ExternalAppFailureTranslator.ForMissingCapabilities(admission.MissingCapabilities).Summary,
            // The resolution's own prose, never a second description of the same state written here.
            ExternalAppBlockedReason.RuntimeUnavailable => admission.Runtime.Message,
            ExternalAppBlockedReason.InsufficientMemory or ExternalAppBlockedReason.InsufficientDisk => admission.Resources.Message,
            ExternalAppBlockedReason.BridgeUnavailable => BridgeUnavailableDetail,
            _ => "This application cannot be installed on this node right now."
        };

        return Refuse(reason, detail);
    }

    /// <summary>
    ///     The one composition of a refusal, shared with the lifecycle admission, which holds a detail but no install
    ///     admission to report from. The reason NAME leads the message: the 400 body carries prose only, so the
    ///     operator reads the category there rather than from a typed member no layer surfaces.
    /// </summary>
    private static ExternalAppValidationException Refuse(ExternalAppBlockedReason reason, string detail)
    {
        return new ExternalAppValidationException($"{reason}: {detail}");
    }

    private static void RequireFingerprint(ApplicationManifest manifest, int manifestVersion, string manifestSha256)
    {
        if (manifest.ManifestVersion == manifestVersion
            && string.Equals(manifest.ManifestSha256, manifestSha256, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new ExternalAppManifestChangedException("This application's definition changed after it was shown; review it again before continuing.",
            manifest.ManifestVersion,
            manifest.ManifestSha256);
    }

    private static async Task<ApplicationManifest> RequireApplicationAsync(IApplicationCatalogProvider catalog,
        string applicationId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);

        return await catalog.GetApplicationAsync(applicationId, cancellationToken).ConfigureAwait(false)
               ?? throw new ExternalAppNotFoundException($"The catalog declares no application '{applicationId}'.");
    }

    private static string DisplayNameFor(InstallCommand command, ApplicationManifest manifest)
    {
        var chosen = string.IsNullOrWhiteSpace(command.DisplayName) ? manifest.DisplayName : command.DisplayName.Trim();
        return chosen.Length > MaxDisplayNameLength ? chosen[..MaxDisplayNameLength] : chosen;
    }

    private static ExternalAppFailure Cancelled()
    {
        return new ExternalAppFailure(ExternalAppFailureCategory.Unknown,
            "This operation was cancelled before it finished; the application's data was kept.");
    }

    /// <summary>What the four admission inputs said, so the preview and the command read one evaluation.</summary>
    private sealed record InstallAdmission(
        ExternalAppBlockedReason? BlockedReason,
        ContainerRuntimeResolution Runtime,
        IReadOnlyList<string> MissingCapabilities,
        ExternalAppResourceVerdict Resources,
        Guid? ExistingInstanceId);
}
