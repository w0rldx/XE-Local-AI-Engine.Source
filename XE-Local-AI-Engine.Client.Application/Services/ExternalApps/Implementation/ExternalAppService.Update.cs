namespace XE_Local_AI_Engine.Client.Services.ExternalApps.Implementation;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Containers;
using XE_Local_AI_Engine.Client.Services.Containers.Bridge;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container;

/// <summary>
///     The update preview and the update pipeline. This is also the part that attaches
///     <see cref="IExternalAppService" />: the contract is complete only once every member exists, and declaring it
///     earlier would have meant shipping placeholders for the ones that did not.
/// </summary>
internal sealed partial class ExternalAppService : IExternalAppService
{
    public async Task<UpdatePreview> PreviewUpdateAsync(Guid instanceId, CancellationToken cancellationToken = default)
    {
        EnsureEnabled();

        await using var scope = _scopeFactory.CreateAsyncScope();
        var services = ScopedServices.From(scope.ServiceProvider);

        var row = await RequireInstanceAsync(services.Store, instanceId, cancellationToken);
        var installed = DeserializeManifest(row.ManifestSnapshotJson);
        var stored = ParseVariables(row.VariablesJson);
        var target = await services.Catalog.GetApplicationAsync(row.ApplicationId, cancellationToken);

        if (target is null)
        {
            // 200, not 404. A blocked preview is renderable information — the dialog can say WHY there is nothing to
            // update to — where a mutating call against a manifest that no longer exists is not executable.
            var verdict = await _resourceGate.EvaluateAsync(installed, _layout.Root, cancellationToken);
            return new UpdatePreview(row.ApplicationId,
                instanceId,
                row.ManifestVersion,
                row.ManifestVersion,
                installed.ManifestSha256,
                installed.Variables,
                MaskForTarget(installed, installed, stored),
                [],
                ExternalAppEffectivePermissions.From(installed),
                verdict,
                CanUpdate: false,
                ExternalAppBlockedReason.CatalogMissing);
        }

        var admission = await EvaluateTargetAsync(services, target, cancellationToken);
        var newer = target.ManifestVersion > row.ManifestVersion;

        return new UpdatePreview(row.ApplicationId,
            instanceId,
            row.ManifestVersion,
            target.ManifestVersion,
            target.ManifestSha256,
            target.Variables,
            MaskForTarget(installed, target, stored),
            ExternalAppEffectivePermissions.Diff(ExternalAppEffectivePermissions.From(installed), ExternalAppEffectivePermissions.From(target)),
            ExternalAppEffectivePermissions.From(target),
            admission.Resources,
            newer && admission.BlockedReason is null,
            // "Already current" carries no blocked reason: nothing is wrong, and the two version members say it.
            newer ? admission.BlockedReason : null);
    }

    public async Task<ExternalAppInstanceSummary> UpdateAsync(Guid instanceId,
        long expectedVersion,
        UpdateCommand command,
        CancellationToken cancellationToken = default)
    {
        EnsureEnabled();
        ArgumentNullException.ThrowIfNull(command);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var services = ScopedServices.From(scope.ServiceProvider);

        IDisposable? lease = null;
        try
        {
            lease = await _gate.TryEnterAsync(ExternalAppInstanceGate.InstanceKey(instanceId))
                    ?? throw new ExternalAppOperationInFlightException("An operation is already running on this instance.");

            var row = await RequireInstanceAsync(services.Store, instanceId, cancellationToken);
            RequireVersion(row, expectedVersion);

            var admitted = AdmittedStatusFor(ExternalAppOperationKind.Update, row.Status)
                           ?? throw new ExternalAppInvalidTransitionException($"An update is not available while the application is {row.Status}.");

            // The application must still be in the catalog. If it has left, the instance keeps running untouched:
            // there is no target manifest, so there is nothing this could update INTO.
            var target = await services.Catalog.GetApplicationAsync(row.ApplicationId, cancellationToken)
                         ?? throw new ExternalAppNotFoundException($"The catalog no longer declares '{row.ApplicationId}', so this application cannot be updated.");

            var versions = await ReadCatalogVersionsAsync(services.Catalog, cancellationToken);
            if (target.ManifestVersion <= row.ManifestVersion)
            {
                // Already current — also the answer when an offline fallback to the bundled seed serves an OLDER
                // version than the one installed.
                return ToSummary(row, versions);
            }

            // Every install precondition, against the TARGET and BEFORE anything is stopped: an installed
            // application must not be able to update into a manifest this version would refuse to install.
            var admission = await EvaluateTargetAsync(services, target, cancellationToken);
            if (admission.BlockedReason is { } blocked)
            {
                throw Refuse(blocked, admission);
            }

            var installed = DeserializeManifest(row.ManifestSnapshotJson);
            var variables = ValidateVariables(target, Merge(CarryForward(installed, target, ParseVariables(row.VariablesJson)), command.Variables));

            RequireFingerprint(target, command.ManifestVersion, command.ManifestSha256);

            // The BACKFILL, and the admission-time refusal that has to come with it.
            //
            // A row installed before the bridge existed carries no token (Odysseus v4 → v5 is the shape), and a
            // target whose manifest reads a bridge built-in cannot be planned without one. This is the one pipeline
            // that can mint without a migration that writes secrets: it rewrites the row and recreates every
            // container anyway, so CommitUpdateAsync below persists the token in the same transaction as the
            // manifest the containers were built from. Minted whether or not this node has an open bridge, exactly
            // as install mints it — the token is the instance's, and whether it can be USED is the endpoint's
            // question, which BridgeGrantFor asks.
            //
            // The refusal is HERE rather than in the pipeline because a pipeline failure settles the row by removing
            // this instance's containers: a target this node cannot plan would cost a working application the
            // version it was running, and the row would still describe that version, so the retry refuses again.
            var mintedBridgeToken = row.BridgeToken is null ? ContainerBridgeToken.Mint(instanceId) : null;
            var bridgeGrant = BridgeGrantFor(row.BridgeToken ?? mintedBridgeToken);
            RequireTargetIsPlannable(instanceId,
                target,
                variables,
                ExternalAppContainerIdentity.Resolve(admission.Runtime.Daemon.IsRootless, _options.ContainerIdentity),
                bridgeGrant);

            var added = ExternalAppEffectivePermissions.Diff(ExternalAppEffectivePermissions.From(installed),
                ExternalAppEffectivePermissions.From(target));
            if (added.Count > 0 && !command.AcceptPermissions)
            {
                throw new ExternalAppPermissionChangeRequiresAcknowledgementException("This update grants the application permissions it does not have; accept them before continuing.",
                    added);
            }

            var cursor = new InstanceCursor(instanceId, row.Version, row.Status);
            if (added.Count > 0)
            {
                // Written BEFORE the stop, so the acknowledgement is on the record even if the update then fails.
                _ = await ApplyAsync(services.Store,
                        cursor,
                        Transition(cursor, row.Status, ExternalAppInstanceEventKind.PermissionAccepted),
                        cancellationToken);
            }

            if (!await ApplyAsync(services.Store,
                        cursor,
                        Transition(cursor, admitted, ExternalAppInstanceEventKind.UpdateRequested),
                        cancellationToken))
            {
                throw new ExternalAppConcurrencyException("The instance changed while this update was being admitted.");
            }

            var context = new LifecycleContext(instanceId, cursor.Version, cursor.Sequence, admitted, row);
            if (!_runner.TryStart(instanceId,
                    ExternalAppOperationKind.Update,
                    lease,
                    (provider, token) => RunUpdateAsync(provider, context, target, variables, bridgeGrant, mintedBridgeToken, token),
                    out _))
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

    /// <summary>
    ///     Stop, tear down, rebuild against the target, and commit the row in ONE transaction placed after every
    ///     replacement container is created and verified and before any is started.
    /// </summary>
    /// <remarks>
    ///     That placement is the whole recovery story. Before the commit the row still describes the old version and
    ///     the new containers are removed, so the failure is an ordinary one to retry. After it the row describes the
    ///     target and the next start recovers FORWARD onto the new images — the only safe direction once a
    ///     replacement may already have migrated bind-mounted data.
    /// </remarks>
    private async Task RunUpdateAsync(IServiceProvider provider,
        LifecycleContext context,
        ApplicationManifest target,
        IReadOnlyDictionary<string, string> variables,
        ContainerBridgeGrant? bridgeGrant,
        string? mintedBridgeToken,
        CancellationToken cancellationToken)
    {
        var services = ScopedServices.From(provider);
        var cursor = context.ToCursor();
        IContainerRuntime? runtime = null;

        try
        {
            var resolution = await services.Resolver.ResolveAsync(cancellationToken: cancellationToken);
            runtime = await services.Resolver.CreateRuntimeAsync(cancellationToken: cancellationToken);

            await StopInstanceAsync(runtime, context.Row, cancellationToken);

            // Confirmed, not attempted: the rebuild below creates this instance's containers again, and one survivor
            // of the old version would collide with its replacement by name and keep serving the old image.
            await RequireTeardownAsync(runtime, context.InstanceId, cancellationToken);

            var snapshotJson = SerializeManifest(target);
            var variablesJson = SerializeVariables(variables);
            var committed = false;

            var published = await RebuildAsync(runtime,
                    resolution.Daemon.IsRootless,
                    context.InstanceId,
                    target,
                    variables,
                    bridgeGrant,
                    async (planned, token) =>
                    {
                        if (committed)
                        {
                            // A replanned attempt reaches this point a second time. The
                            // row already describes the target, and re-committing would
                            // lose its own compare-and-swap against the version it set.
                            return;
                        }

                        var result = await services.Store.CommitUpdateAsync(context.InstanceId,
                                                       cursor.Version,
                                                       snapshotJson,
                                                       variablesJson,
                                                       ExternalAppPublishedPorts.Serialize(planned),
                                                       target.ManifestVersion,
                                                       Now(),
                                                       mintedBridgeToken,
                                                       token);

                        if (!result.Applied)
                        {
                            throw new ExternalAppPipelineException(new ExternalAppFailure(ExternalAppFailureCategory.Unknown,
                                "This application changed while it was being updated; nothing was started."));
                        }

                        cursor.Version = result.Version;
                        cursor.Sequence = result.Sequence;
                        committed = true;
                    },
                    cancellationToken);

            // The desired state is the user's and an update is not a decision to start something that was stopped.
            // Against the TARGET manifest: the containers standing here are the ones the rebuild just created, so the
            // installed snapshot's service list would leave anything the target added or renamed running.
            var restoreStopped = context.Row.DesiredState == ExternalAppDesiredState.Stopped;
            if (restoreStopped)
            {
                await StopInstanceAsync(runtime, context.InstanceId, target, cancellationToken);
            }

            _ = await ApplyAsync(services.Store,
                    cursor,
                    Transition(cursor,
                            restoreStopped ? ExternalAppInstanceStatus.Stopped : ExternalAppInstanceStatus.Running,
                            ExternalAppInstanceEventKind.Updated) with
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
            await SettleFailureAsync(services.Store, runtime, cursor, exception);
        }
        finally
        {
            await DisposeRuntimeAsync(runtime);
        }
    }

    /// <summary>
    ///     Whether the target manifest can be planned at all, asked at admission and only for the answer — the plan
    ///     itself is discarded and the pipeline builds its own.
    ///     <para>
    ///         The host ports are placeholders, and they are enough: the pipeline holds real ones, and the planner
    ///         only substitutes their numbers into <c>XE_UI_HOST_PORT_&lt;service&gt;</c>. What is being asked is
    ///         whether the target RESOLVES — the bridge built-ins above all — and that does not depend on which port
    ///         the allocator would hand out. Binding real ports to answer it would make admission fail for a reason
    ///         that has nothing to do with the question.
    ///     </para>
    ///     <para>
    ///         The refusal families are the ones <c>TryPlanForVerification</c> already treats as "this cannot be
    ///         planned as it stands", re-raised as the validation exception the API renders as a 400 so the operator
    ///         reads the planner's own message: for the bridge case, which feature is missing and which setting
    ///         turns it on.
    ///     </para>
    /// </summary>
    private void RequireTargetIsPlannable(Guid instanceId,
        ApplicationManifest target,
        IReadOnlyDictionary<string, string> variables,
        ResolvedContainerIdentity identity,
        ContainerBridgeGrant? bridgeGrant)
    {
        var hostPorts = target.Services
                              .SelectMany(service => service.Ports
                                                            .Where(static port => string.Equals(port.Role, UiPortRole, StringComparison.Ordinal))
                                                            .Select(port => new ExternalAppHostPort(service.Name, port.ContainerPort, port.ContainerPort)))
                              .ToList();

        try
        {
            _ = DeploymentPlanner.Plan(target, instanceId, _installId, variables, identity, hostPorts, _layout.Describe(instanceId), bridgeGrant);
        }
        catch (Exception exception) when (exception is ExternalAppConfigurationException or ExternalAppManifestException or ContainerPolicyException)
        {
            throw new ExternalAppValidationException(exception.Message, exception);
        }
    }

    /// <summary>Steps 2–4 against a target manifest: the runtime, the GPU rule, the capabilities and the resources.</summary>
    private async Task<InstallAdmission> EvaluateTargetAsync(ScopedServices services,
        ApplicationManifest target,
        CancellationToken cancellationToken)
    {
        var resolution = await services.Resolver.ResolveAsync(cancellationToken: cancellationToken);
        var missing = resolution.Capabilities.FindMissing(target.Requires);
        var resources = await _resourceGate.EvaluateAsync(target, _layout.Root, cancellationToken);

        // alreadyInstalled is false on purpose: this application IS installed, and that is the precondition of an
        // update rather than a reason to refuse one.
        return new InstallAdmission(BlockingReason(alreadyInstalled: false, BridgeUnavailableFor(target), resolution, target, missing, resources),
            resolution,
            missing,
            resources,
            ExistingInstanceId: null);
    }

    /// <summary>
    ///     The stored values an update may carry forward: the ones the TARGET still declares, minus the ones whose
    ///     classification changed.
    ///     <para>
    ///         A variable the target removed or renamed is dropped rather than carried. Validation refuses an
    ///         undeclared key on purpose — a mistyped password field must never be installed blank — but a key the
    ///         node itself stored under the previous manifest is not a user's typo, and carrying it made every
    ///         update to a manifest that dropped a variable refuse itself over a value nobody submitted.
    ///     </para>
    ///     <para>
    ///         A variable that was <c>secret</c> in the installed snapshot and is not in the target is dropped too:
    ///         keeping it would let a manifest change turn a password into an ordinary string the node hands back
    ///         in plaintext.
    ///     </para>
    /// </summary>
    private static IReadOnlyDictionary<string, string> CarryForward(ApplicationManifest installed,
        ApplicationManifest target,
        IReadOnlyDictionary<string, string> stored)
    {
        var carried = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in stored)
        {
            if (!Declares(target, entry.Key))
            {
                continue;
            }

            if (WasSecret(installed, entry.Key) && !WasSecret(target, entry.Key))
            {
                continue;
            }

            carried[entry.Key] = entry.Value;
        }

        return carried;
    }

    private static bool Declares(ApplicationManifest manifest, string name)
    {
        return manifest.Variables.Any(variable => string.Equals(variable.Name, name, StringComparison.Ordinal));
    }

    /// <summary>
    ///     The current values as the update dialog may see them: masked where the TARGET calls them secret, absent
    ///     where the target reclassified a secret into something plain.
    /// </summary>
    private static IReadOnlyDictionary<string, string> MaskForTarget(ApplicationManifest installed,
        ApplicationManifest target,
        IReadOnlyDictionary<string, string> stored)
    {
        var masked = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in stored)
        {
            if (WasSecret(target, entry.Key))
            {
                masked[entry.Key] = ExternalAppVariableMask.Value;
                continue;
            }

            if (WasSecret(installed, entry.Key))
            {
                // Treated as unset. If the target marks it required, the preview asks for it again.
                continue;
            }

            masked[entry.Key] = entry.Value;
        }

        return masked;
    }

    private static bool WasSecret(ApplicationManifest manifest, string name)
    {
        return manifest.Variables.Any(variable => string.Equals(variable.Name, name, StringComparison.Ordinal)
                                                  && string.Equals(variable.Type, SecretVariableType, StringComparison.Ordinal));
    }
}
