namespace XE_Local_AI_Engine.Client.Services.ModelFit.Implementation;

using System.Runtime.InteropServices;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.LlamaCpp;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;

internal sealed class LlamaCppRuntimeAdministrationService : ILlamaCppRuntimeAdministrationService
{
    private const string KeepModelWarmBlockedMessage =
        "Disable Keep Model Warm before changing the llama.cpp runtime, then eject any running models and retry.";

    private static readonly TimeSpan MinRefreshInterval = TimeSpan.FromSeconds(60);
    private readonly Lock _taskGate = new();
    private readonly TimeProvider _timeProvider;
    private readonly ILlamaCppBinaryManager _binaryManager;
    private readonly ILlamaCppReleaseCatalog _releaseCatalog;
    private readonly IGpuVariantSelector _variantSelector;
    private readonly IInstalledRuntimeStore _installedRuntimeStore;
    private readonly ILlamaCppSourceBuildActivity _sourceBuildActivity;
    private readonly ILlamaCppUpdateState _updateState;
    private readonly IRuntimeAcquisitionStatusRegistry _acquisitionStatus;
    private readonly INodeRuntimeSettings _nodeRuntimeSettings;
    private readonly ILlamaServerProcessSupervisor _processSupervisor;
    private readonly ILocalChatClientCacheInvalidator _localChatClientCacheInvalidator;
    private readonly LlamaServerRuntimeOverrideOptions _overrideOptions;
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly ILogger<LlamaCppRuntimeAdministrationService> _logger;
    private Task? _ownedAcquisitionTask;

    public LlamaCppRuntimeAdministrationService(
        ILlamaCppBinaryManager binaryManager,
        ILlamaCppReleaseCatalog releaseCatalog,
        IGpuVariantSelector variantSelector,
        IInstalledRuntimeStore installedRuntimeStore,
        ILlamaCppSourceBuildActivity sourceBuildActivity,
        ILlamaCppUpdateState updateState,
        IRuntimeAcquisitionStatusRegistry acquisitionStatus,
        INodeRuntimeSettings nodeRuntimeSettings,
        ILlamaServerProcessSupervisor processSupervisor,
        ILocalChatClientCacheInvalidator localChatClientCacheInvalidator,
        LlamaServerRuntimeOverrideOptions overrideOptions,
        IHostApplicationLifetime applicationLifetime,
        ILogger<LlamaCppRuntimeAdministrationService> logger,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _binaryManager = binaryManager;
        _releaseCatalog = releaseCatalog;
        _variantSelector = variantSelector;
        _installedRuntimeStore = installedRuntimeStore;
        _sourceBuildActivity = sourceBuildActivity;
        _updateState = updateState;
        _acquisitionStatus = acquisitionStatus;
        _nodeRuntimeSettings = nodeRuntimeSettings;
        _processSupervisor = processSupervisor;
        _localChatClientCacheInvalidator = localChatClientCacheInvalidator;
        _overrideOptions = overrideOptions;
        _applicationLifetime = applicationLifetime;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    public async Task<LlamaCppRuntimeStatus> GetStatusAsync(bool refresh = false, CancellationToken cancellationToken = default)
    {
        var recommendedTag = await _nodeRuntimeSettings.GetRecommendedLlamaCppTagAsync(cancellationToken);
        var installed = await _installedRuntimeStore.ReadAsync(cancellationToken);
        var current = _updateState.Current;
        var snapshot = refresh && IsStale(current.CheckedAtUtc, _timeProvider.GetUtcNow())
            ? await ComputeFreshSnapshotAsync(recommendedTag, installed?.Tag, cancellationToken)
            : current;

        return new LlamaCppRuntimeStatus(installed is null ? null : ToView(installed),
            recommendedTag,
            snapshot.UpstreamLatestTag,
            snapshot.UpdateAvailable,
            snapshot.IsOffline,
            _processSupervisor.CountRunningProcesses(),
            snapshot.CheckedAtUtc);
    }

    public LlamaCppRuntimeAcquisitionStatus GetAcquisitionStatus()
    {
        var current = _acquisitionStatus.Current;
        return new LlamaCppRuntimeAcquisitionStatus(current.Sequence,
            current.Phase,
            current.Variant,
            current.Tag,
            current.CompletedBytes,
            current.TotalBytes,
            current.StepIndex,
            current.StepCount,
            current.SanitizedError);
    }

    public async Task<LlamaCppRuntimeMutationResult> EnsureAsync(GpuVariant variant,
        CancellationToken cancellationToken = default)
    {
        var admission = await TryAcquirePrebuiltMutationAsync(cancellationToken);
        await using var lease = admission.Lease;
        if (lease is null || admission.BlockedMessage is not null)
        {
            return LlamaCppRuntimeMutationResult.Rejected(LlamaCppRuntimeAdministrationFailure.Busy,
                admission.BlockedMessage ?? "The llama.cpp runtime is busy with another build or runtime change.",
                admission.RunningProcessCount);
        }

        try
        {
            var binary = await _binaryManager.EnsureBinaryAsync(variant, lease, cancellationToken);
            var recommendedTag = await _nodeRuntimeSettings.GetRecommendedLlamaCppTagAsync(cancellationToken);
            return LlamaCppRuntimeMutationResult.Success(ToView(binary), recommendedTag);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (LlamaRuntimeException exception)
        {
            _logger.LogWarning(exception, "Ensuring the llama.cpp binary for variant {Variant} failed.", variant);
            return LlamaCppRuntimeMutationResult.Rejected(LlamaCppRuntimeAdministrationFailure.RuntimeFailure, exception.Message);
        }
    }

    public async Task<LlamaCppRuntimeMutationResult> InstallAsync(string tag,
        GpuVariant? variant = null,
        CancellationToken cancellationToken = default)
    {
        if (_overrideOptions.IsActive)
        {
            return LlamaCppRuntimeMutationResult.Rejected(LlamaCppRuntimeAdministrationFailure.Busy,
                "Runtime updates are disabled while a bring-your-own llama-server override is active; the operator manages the binary.");
        }

        if (!StoredNodeSettings.IsValidRecommendedLlamaCppTag(tag))
        {
            return LlamaCppRuntimeMutationResult.Rejected(LlamaCppRuntimeAdministrationFailure.InvalidRequest,
                "Tag must be a llama.cpp release tag in the form b<number>.");
        }

        var canonicalTag = tag.Trim();
        if (await _nodeRuntimeSettings.GetKeepModelWarmEnabledAsync(cancellationToken))
        {
            return LlamaCppRuntimeMutationResult.Rejected(LlamaCppRuntimeAdministrationFailure.Busy,
                KeepModelWarmBlockedMessage,
                _processSupervisor.CountRunningProcesses());
        }

        var selectedVariant = variant ?? await _variantSelector.SelectVariantAsync(cancellationToken);
        try
        {
            var asset = await _releaseCatalog.ResolveAssetAsync(canonicalTag,
                CurrentOsPlatform(),
                RuntimeInformation.OSArchitecture,
                selectedVariant,
                cancellationToken);
            if (asset.Asset is null)
            {
                return LlamaCppRuntimeMutationResult.Rejected(LlamaCppRuntimeAdministrationFailure.InvalidRequest,
                    "The llama.cpp runtime catalog is unavailable or has no matching asset for the requested tag.");
            }

            var admission = await TryAcquirePrebuiltMutationAsync(cancellationToken);
            await using var lease = admission.Lease;
            if (lease is null || admission.BlockedMessage is not null)
            {
                return LlamaCppRuntimeMutationResult.Rejected(LlamaCppRuntimeAdministrationFailure.Busy,
                    admission.BlockedMessage ?? "The llama.cpp runtime is busy with another build or runtime change.",
                    admission.RunningProcessCount);
            }

            var binary = await _binaryManager.InstallTagAsync(canonicalTag,
                asset.Asset.Name,
                asset.Asset.Digest,
                asset.Asset.Size,
                selectedVariant,
                lease,
                cancellationToken);
            _localChatClientCacheInvalidator.ClearClientCache();
            await RefreshSnapshotAsync(canonicalTag, cancellationToken);
            var recommendedTag = await _nodeRuntimeSettings.GetRecommendedLlamaCppTagAsync(cancellationToken);
            return LlamaCppRuntimeMutationResult.Success(ToView(binary), recommendedTag);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (LlamaRuntimeException exception)
        {
            _logger.LogWarning(exception, "Installing the llama.cpp runtime for tag {Tag} failed.", canonicalTag);
            return LlamaCppRuntimeMutationResult.Rejected(LlamaCppRuntimeAdministrationFailure.RuntimeFailure, exception.Message);
        }
    }

    public async Task<LlamaCppRuntimeAcquisitionStartResult> StartAcquisitionAsync(GpuVariant? variant = null,
        CancellationToken cancellationToken = default)
    {
        var selectedVariant = variant ?? await _variantSelector.SelectVariantAsync(cancellationToken);
        var admission = await TryAcquirePrebuiltMutationAsync(cancellationToken);
        if (admission.Lease is null || admission.BlockedMessage is not null)
        {
            if (admission.Lease is not null)
            {
                await admission.Lease.DisposeAsync();
            }

            return new LlamaCppRuntimeAcquisitionStartResult(false,
                ToWireString(selectedVariant),
                LlamaCppRuntimeAdministrationFailure.Busy,
                admission.BlockedMessage ?? "The llama.cpp runtime is busy with another build or runtime change.",
                admission.RunningProcessCount);
        }

        Task ownedTask;
        lock (_taskGate)
        {
            ownedTask = RunOwnedAcquisitionAsync(selectedVariant, admission.Lease, _applicationLifetime.ApplicationStopping);
            _ownedAcquisitionTask = ownedTask;
        }

        _ = ObserveOwnedAcquisitionAsync(ownedTask, selectedVariant);
        return new LlamaCppRuntimeAcquisitionStartResult(true,
            ToWireString(selectedVariant),
            LlamaCppRuntimeAdministrationFailure.None,
            DisplayMessage: null);
    }

    private async Task<PrebuiltMutationAdmission> TryAcquirePrebuiltMutationAsync(CancellationToken cancellationToken)
    {
        if (await _nodeRuntimeSettings.GetKeepModelWarmEnabledAsync(cancellationToken))
        {
            return new PrebuiltMutationAdmission(null, _processSupervisor.CountRunningProcesses(), KeepModelWarmBlockedMessage);
        }

        var lease = await _processSupervisor.TryAcquireRuntimeMutationLeaseAsync(cancellationToken);
        if (lease is null)
        {
            return new PrebuiltMutationAdmission(null,
                _processSupervisor.CountRunningProcesses(),
                "The llama.cpp runtime is busy with another build or runtime change. Try again after it completes.");
        }

        var transferred = false;
        try
        {
            var installed = await _installedRuntimeStore.ReadAsync(cancellationToken);
            if (installed?.SourceBuildPath is { Length: > 0 })
            {
                return new PrebuiltMutationAdmission(null,
                    _processSupervisor.CountRunningProcesses(),
                    "Remove the installed source-built llama.cpp runtime before installing a prebuilt runtime.");
            }

            if (_sourceBuildActivity.ActiveBuildId is not null)
            {
                return new PrebuiltMutationAdmission(null,
                    _processSupervisor.CountRunningProcesses(),
                    "Wait for the active llama.cpp source build to finish or cancel it before installing a prebuilt runtime.");
            }

            var runningProcessCount = _processSupervisor.CountRunningProcesses();
            if (runningProcessCount > 0)
            {
                return new PrebuiltMutationAdmission(null,
                    runningProcessCount,
                    "Stop or eject all running llama.cpp models before updating the runtime.");
            }

            transferred = true;
            return new PrebuiltMutationAdmission(lease, runningProcessCount, BlockedMessage: null);
        }
        finally
        {
            if (!transferred)
            {
                await lease.DisposeAsync();
            }
        }
    }

    private async Task RunOwnedAcquisitionAsync(GpuVariant variant,
        ILlamaServerRuntimeMutationLease lease,
        CancellationToken applicationStopping)
    {
        await using (lease)
        {
            await _binaryManager.EnsureBinaryAsync(variant, lease, applicationStopping);
        }
    }

    private async Task ObserveOwnedAcquisitionAsync(Task task, GpuVariant variant)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException) when (_applicationLifetime.ApplicationStopping.IsCancellationRequested)
        {
            _logger.LogInformation("llama.cpp runtime acquisition for {Variant} stopped with the host.", variant);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Detached llama.cpp runtime acquisition for {Variant} failed.", variant);
        }
        finally
        {
            lock (_taskGate)
            {
                if (ReferenceEquals(_ownedAcquisitionTask, task))
                {
                    _ownedAcquisitionTask = null;
                }
            }
        }
    }

    private async Task<LlamaCppUpdateSnapshot> ComputeFreshSnapshotAsync(string recommendedTag,
        string? installedTag,
        CancellationToken cancellationToken)
    {
        var recommendedResult = await _releaseCatalog.ResolveRecommendedAsync(recommendedTag, cancellationToken);
        var upstreamResult = await _releaseCatalog.ResolveUpstreamLatestAsync(cancellationToken);
        var resolvedRecommended = recommendedResult.Tag;
        var snapshot = new LlamaCppUpdateSnapshot(installedTag,
            resolvedRecommended ?? recommendedTag,
            upstreamResult.Tag,
            LlamaCppRuntimeTag.IsUpdateAvailable(installedTag, resolvedRecommended),
            recommendedResult.IsOffline || recommendedResult.IsRateLimited,
            _timeProvider.GetUtcNow());
        _updateState.Store(snapshot);
        return snapshot;
    }

    private async Task RefreshSnapshotAsync(string installedTag, CancellationToken cancellationToken)
    {
        var recommendedTag = await _nodeRuntimeSettings.GetRecommendedLlamaCppTagAsync(cancellationToken);
        var installed = await _installedRuntimeStore.ReadAsync(cancellationToken);
        var effectiveInstalledTag = installed?.Tag ?? installedTag;
        var previous = _updateState.Current;
        _updateState.Store(new LlamaCppUpdateSnapshot(effectiveInstalledTag,
            recommendedTag,
            previous.UpstreamLatestTag,
            LlamaCppRuntimeTag.IsUpdateAvailable(effectiveInstalledTag, recommendedTag),
            IsOffline: false,
            _timeProvider.GetUtcNow()));
    }

    private static bool IsStale(DateTimeOffset? checkedAtUtc, DateTimeOffset now) =>
        checkedAtUtc is not { } checkedAt || now - checkedAt >= MinRefreshInterval;

    private static OSPlatform CurrentOsPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            return OSPlatform.Windows;
        }

        return OperatingSystem.IsMacOS() ? OSPlatform.OSX : OSPlatform.Linux;
    }

    private static LlamaCppRuntimeBinaryView ToView(LlamaBinary binary) =>
        new(binary.Version, ToWireString(binary.Variant), binary.IsPinnedFallback);

    private static LlamaCppInstalledRuntimeView ToView(InstalledRuntimeState installed) =>
        new(installed.Tag,
            installed.Asset,
            ToWireString(installed.Variant),
            installed.InstalledAtUtc.ToUnixTimeMilliseconds(),
            installed.SourceBuildPath is { Length: > 0 },
            installed.SourceRepository,
            installed.SourceCommit,
            installed.SourceRevisionMode is null ? null : (int)installed.SourceRevisionMode.Value,
            installed.SourceRequestedCommit,
            installed.SourceSelection is null ? null : (int)installed.SourceSelection.Value);

    private static string ToWireString(GpuVariant variant) =>
        variant switch
        {
            GpuVariant.Cpu => "cpu",
            GpuVariant.Cuda => "cuda",
            GpuVariant.Vulkan => "vulkan",
            _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, "Unknown runtime variant.")
        };

    private sealed record PrebuiltMutationAdmission(
        ILlamaServerRuntimeMutationLease? Lease,
        int RunningProcessCount,
        string? BlockedMessage);
}
