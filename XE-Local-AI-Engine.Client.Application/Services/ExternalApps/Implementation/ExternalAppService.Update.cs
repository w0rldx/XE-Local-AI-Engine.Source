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
            return new UpdatePreview
            {
                ApplicationId = row.ApplicationId,
                InstanceId = instanceId,
                CurrentManifestVersion = row.ManifestVersion,
                TargetManifestVersion = row.ManifestVersion,
                ManifestSha256 = installed.ManifestSha256,
                Variables = installed.Variables,
                CurrentValues = MaskForTarget(installed, installed, stored),
                AddedPermissions = [],
                EffectivePermissions = ExternalAppEffectivePermissions.From(installed),
                ResourceVerdict = verdict,
                CanUpdate = false,
                BlockedReason = ExternalAppBlockedReason.CatalogMissing
            };
        }

        var admission = await EvaluateTargetAsync(services, target, cancellationToken);
        var newer = target.ManifestVersion > row.ManifestVersion;

        return new UpdatePreview
        {
            ApplicationId = row.ApplicationId,
            InstanceId = instanceId,
            CurrentManifestVersion = row.ManifestVersion,
            TargetManifestVersion = target.ManifestVersion,
            ManifestSha256 = target.ManifestSha256,
            Variables = target.Variables,
            CurrentValues = MaskForTarget(installed, target, stored),
            AddedPermissions = ExternalAppEffectivePermissions.Diff(ExternalAppEffectivePermissions.From(installed), ExternalAppEffectivePermissions.From(target)),
            EffectivePermissions = ExternalAppEffectivePermissions.From(target),
            ResourceVerdict = admission.Resources,
            CanUpdate = newer && admission.BlockedReason is null,
            // "Already current" carries no blocked reason: nothing is wrong, and the two version members say it.
            BlockedReason = newer ? admission.BlockedReason : null
        };
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

            // The BACKFILL: a row installed before the bridge existed carries no token, and update is the only
            // pipeline that mints one. Why, and why its refusal sits at admission: ADR 0011, the backfill section.
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

            var context = new LifecycleContext { InstanceId = instanceId, Version = cursor.Version, Sequence = cursor.Sequence, Status = admitted, Row = row };
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
                            // A replanned attempt reaches this a second time: the row already describes the target,
                            // so re-committing would lose its own compare-and-swap against the version it set.
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
                            throw new ExternalAppPipelineException(new ExternalAppFailure
                            {
                                Category = ExternalAppFailureCategory.Unknown,
                                Summary = "This application changed while it was being updated; nothing was started."
                            });
                        }

                        cursor.Version = result.Version;
                        cursor.Sequence = result.Sequence;
                        committed = true;
                    },
                    cancellationToken);

            // The desired state is the user's: an update does not start what was stopped. Stopped against the TARGET
            // manifest, since the containers standing here are the rebuild's and the target may have added services.
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

    /// <summary>Whether the target manifest can be planned at all, asked at admission and only for the answer: the plan itself is discarded and the pipeline builds its own.</summary>
    /// <remarks>
    ///     The host ports are placeholders and they are enough, because the planner only substitutes their numbers
    ///     into <c>XE_UI_HOST_PORT_&lt;service&gt;</c> while what is asked is whether the target RESOLVES — the
    ///     bridge built-ins above all. Binding real ports would make admission fail for an unrelated reason. The
    ///     refusal families are the ones <c>TryPlanForVerification</c> treats as "cannot be planned as it stands",
    ///     re-raised as the validation exception the API renders as a 400 carrying the planner's own message.
    /// </remarks>
    private void RequireTargetIsPlannable(Guid instanceId,
        ApplicationManifest target,
        IReadOnlyDictionary<string, string> variables,
        ResolvedContainerIdentity identity,
        ContainerBridgeGrant? bridgeGrant)
    {
        var hostPorts = target.Services
                              .SelectMany(service => service.Ports
                                                            .Where(static port => string.Equals(port.Role, UiPortRole, StringComparison.Ordinal))
                                                            .Select(port => new ExternalAppHostPort { Service = service.Name, ContainerPort = port.ContainerPort, HostPort = port.ContainerPort }))
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
        return new InstallAdmission
        {
            BlockedReason = BlockingReason(alreadyInstalled: false, BridgeUnavailableFor(target), resolution, target, missing, resources),
            Runtime = resolution,
            MissingCapabilities = missing,
            Resources = resources,
            ExistingInstanceId = null
        };
    }

    /// <summary>The stored values an update may carry forward: the ones the TARGET still declares, minus the ones whose classification changed.</summary>
    /// <remarks>
    ///     A variable the target removed or renamed is dropped, not carried: validation refuses an undeclared key on
    ///     purpose — a mistyped password field must never install blank — but a key the node itself stored under the
    ///     previous manifest is not a typo, and carrying it made every update dropping a variable refuse itself over
    ///     a value nobody submitted. One that was <c>secret</c> in the installed snapshot and is not in the target is
    ///     dropped too, so no manifest change turns a password into a string the node hands back in plaintext.
    /// </remarks>
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
