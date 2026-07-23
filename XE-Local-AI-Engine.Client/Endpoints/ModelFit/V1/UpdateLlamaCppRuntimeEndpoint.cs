namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using System.Runtime.InteropServices;
using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.LlamaCpp;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Configuration;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Operator-initiated install/update of a chosen llama.cpp release tag (POST model-fit/llamacpp/update). Validates the
///     <c>tag</c> against <c>^b\d+$</c> (400 on a bad tag), resolves the acceleration variant (request override or the
///     auto-selected host variant), resolves the per-variant asset name + publisher digest from the live release catalog,
///     then installs via <see cref="ILlamaCppBinaryManager.InstallTagAsync" /> (download → digest-verify → atomic extract
///     → smoke test → record). On success it refreshes the shared update snapshot and returns the resolved binary.
///     <para>
///         Errors are sanitized: a malformed tag, an offline/unresolvable catalog, or a failed install surface a 400 with
///         a user-safe message (no internal path/URL/secret) following the existing <see cref="LlamaRuntimeException" />
///         catch posture.
///     </para>
/// </summary>
public sealed class UpdateLlamaCppRuntimeEndpoint(
    ILlamaCppBinaryManager binaryManager,
    ILlamaCppReleaseCatalog releaseCatalog,
    IGpuVariantSelector variantSelector,
    IInstalledRuntimeStore installedRuntimeStore,
    ILlamaCppSourceBuildActivity sourceBuildActivity,
    ILlamaCppUpdateState updateState,
    INodeRuntimeSettings nodeRuntimeSettings,
    ILlamaServerProcessSupervisor processSupervisor,
    ILocalChatClientCacheInvalidator localChatClientCacheInvalidator,
    LlamaServerRuntimeOverrideOptions overrideOptions,
    ILogger<UpdateLlamaCppRuntimeEndpoint> logger) : Endpoint<UpdateLlamaCppRuntimeRequest, LlamaCppVersionResponse>
{
    private readonly ILlamaCppBinaryManager _binaryManager = binaryManager ?? throw new ArgumentNullException(nameof(binaryManager));
    private readonly LlamaServerRuntimeOverrideOptions _overrideOptions = overrideOptions ?? throw new ArgumentNullException(nameof(overrideOptions));
    private readonly IInstalledRuntimeStore _installedRuntimeStore = installedRuntimeStore ?? throw new ArgumentNullException(nameof(installedRuntimeStore));
    private readonly ILocalChatClientCacheInvalidator _localChatClientCacheInvalidator = localChatClientCacheInvalidator ?? throw new ArgumentNullException(nameof(localChatClientCacheInvalidator));
    private readonly ILogger<UpdateLlamaCppRuntimeEndpoint> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly INodeRuntimeSettings _nodeRuntimeSettings = nodeRuntimeSettings ?? throw new ArgumentNullException(nameof(nodeRuntimeSettings));
    private readonly ILlamaServerProcessSupervisor _processSupervisor = processSupervisor ?? throw new ArgumentNullException(nameof(processSupervisor));
    private readonly ILlamaCppReleaseCatalog _releaseCatalog = releaseCatalog ?? throw new ArgumentNullException(nameof(releaseCatalog));
    private readonly ILlamaCppSourceBuildActivity _sourceBuildActivity = sourceBuildActivity ?? throw new ArgumentNullException(nameof(sourceBuildActivity));
    private readonly ILlamaCppUpdateState _updateState = updateState ?? throw new ArgumentNullException(nameof(updateState));
    private readonly IGpuVariantSelector _variantSelector = variantSelector ?? throw new ArgumentNullException(nameof(variantSelector));

    public override void Configure()
    {
        Post(LocalApiRoutes.ModelFit.LlamaCppUpdate);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Produces<LlamaCppVersionResponse>(StatusCodes.Status200OK)
                               .ProducesProblemFE(StatusCodes.Status400BadRequest)
                               .Produces<LlamaCppUpdateBlockedResponse>(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(UpdateLlamaCppRuntimeRequest req, CancellationToken ct)
    {
        // Bring-your-own override active: the operator manages the binary out-of-band, so the catalog-driven update path is
        // disabled. Short-circuit with an explicit, sanitized 409 rather than auto-selecting the override variant (which
        // would resolve no catalog asset and surface a misleading "no matching asset" error). RunningProcessCount is 0 —
        // this block is the override, not the eject-first safety gate.
        if (_overrideOptions.IsActive)
        {
            await Send.ResultAsync(Results.Conflict(new LlamaCppUpdateBlockedResponse
            {
                RunningProcessCount = 0,
                Message = "Runtime updates are disabled while a bring-your-own llama-server override is active; the operator manages the binary."
            })).ConfigureAwait(false);
            return;
        }

        // Tag-format gate at the transport boundary — rejects path/URL injection before any URL is composed.
        if (!StoredNodeSettings.IsValidRecommendedLlamaCppTag(req.Tag))
        {
            AddError(r => r.Tag, "Tag must be a llama.cpp release tag in the form b<number>.");
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        var tag = req.Tag.Trim();

        // A supplied variant override must parse; otherwise auto-select the host variant.
        GpuVariant variant;
        if (req.Variant is null)
        {
            variant = await _variantSelector.SelectVariantAsync(ct).ConfigureAwait(false);
        }
        else if (ModelFitMapper.TryParseVariant(req.Variant) is { } parsed)
        {
            variant = parsed;
        }
        else
        {
            AddError(r => r.Variant, "Variant is not supported.");
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        try
        {
            var asset = await _releaseCatalog
                              .ResolveAssetAsync(tag, CurrentOsPlatform(), RuntimeInformation.OSArchitecture, variant, ct)
                              .ConfigureAwait(false);

            // The catalog is offline/rate-limited or no matching asset exists for this tag/variant — sanitized 400.
            if (asset.Asset is null)
            {
                AddError("The llama.cpp runtime catalog is unavailable or has no matching asset for the requested tag.");
                await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
                return;
            }

            var (mutationLease, runningProcessCount, blockedMessage) = await LlamaCppPrebuiltRuntimeMutationGuard
                                                                             .TryAcquireAsync(_installedRuntimeStore, _sourceBuildActivity, _processSupervisor, ct)
                                                                             .ConfigureAwait(false);
            await using var ownedMutationLease = mutationLease;
            if (mutationLease is null || blockedMessage is not null)
            {
                await Send.ResultAsync(Results.Conflict(new LlamaCppUpdateBlockedResponse
                {
                    RunningProcessCount = runningProcessCount,
                    Message = blockedMessage ?? "The llama.cpp runtime is busy with another build or runtime change."
                })).ConfigureAwait(false);
                return;
            }

            var binary = await _binaryManager
                               .InstallTagAsync(tag, asset.Asset.Name, asset.Asset.Digest, asset.Asset.Size, variant, mutationLease, ct)
                               .ConfigureAwait(false);

            // The runtime binary has been replaced. The local chat-client router caches a deferred client per
            // (provider, model) that resolved its llama-server endpoint against the previous binary; the eject-first gate
            // above guarantees no process is running now, but the cached client still points at the now-gone endpoint and
            // would connection-time-out on the next send. Clear the cache so the next send re-resolves and ensure-runs the
            // backing process against the freshly installed binary.
            _localChatClientCacheInvalidator.ClearClientCache();

            await RefreshSnapshotAsync(tag, ct).ConfigureAwait(false);

            var recommendedTag = await _nodeRuntimeSettings.GetRecommendedLlamaCppTagAsync(ct).ConfigureAwait(false);
            await Send.OkAsync(binary.ToResponse(recommendedTag), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (LlamaRuntimeException exception)
        {
            // Contractually sanitized message (no path/URL/secret) — surface as a 400 so the panel can show the reason.
            _logger.LogWarning(exception, "Installing the llama.cpp runtime for tag {Tag} failed.", tag);
            AddError(exception.Message);
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
        }
    }

    // After a successful install the installed tag now equals the requested tag, so a subsequent recommended==installed
    // comparison clears "update available". Recompute the snapshot from the freshly-written installed-runtime record.
    private async Task RefreshSnapshotAsync(string installedTag, CancellationToken ct)
    {
        var recommendedTag = await _nodeRuntimeSettings.GetRecommendedLlamaCppTagAsync(ct).ConfigureAwait(false);
        var installed = await _installedRuntimeStore.ReadAsync(ct).ConfigureAwait(false);
        var effectiveInstalledTag = installed?.Tag ?? installedTag;

        var previous = _updateState.Current;
        _updateState.Store(new LlamaCppUpdateSnapshot(effectiveInstalledTag,
            recommendedTag,
            previous.UpstreamLatestTag,
            UpdateAvailable: LlamaCppRuntimeTag.IsUpdateAvailable(effectiveInstalledTag, recommendedTag),
            IsOffline: false,
            CheckedAtUtc: DateTimeOffset.UtcNow));
    }

    private static OSPlatform CurrentOsPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            return OSPlatform.Windows;
        }

        return OperatingSystem.IsMacOS() ? OSPlatform.OSX : OSPlatform.Linux;
    }
}
