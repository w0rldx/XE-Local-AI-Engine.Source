namespace XE_Local_AI_Engine.Client.Services.ModelLab;

using System.Globalization;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Client.Services.Models.Implementation;
using XE_Local_AI_Engine.Client.Services.Training.BaseArtifacts;
using XE_Local_AI_Engine.Client.Services.Training.Runs;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.HuggingFace.Contracts;

/// <summary>
///     Decides before any weight is transferred whether a checkpoint can be traced on this card and paired with an installed GGUF.
/// </summary>
/// <remarks>
///     The fit is the checkpoint's safetensors bytes plus working memory against the card's total, not its live free memory, so a
///     model that is merely loaded is not called too large. The pick is explicit because repository names do not link a third-party
///     GGUF to its base checkpoint. Only <c>config.json</c> is fetched. Accepted ceiling: a GGUF exported with its MTP head counts
///     that block, so it is refused as a block mismatch.
/// </remarks>
public sealed class ModelLabCheckpointPreflightService
{
    /// <summary>What tracing needs beyond the weights: the process, the activations and the capped trace buffers.</summary>
    internal const long WorkingMemoryBytes = 2L * 1024 * 1024 * 1024;

    private const string ConfigFileName = "config.json";

    private const long MaxConfigBytes = 1024 * 1024;

    /// <summary>What <c>modellab.py</c> has traced. Its hooks need <c>model.model</c>'s blocks, which not every layout has; extend this after tracing one.</summary>
    private static readonly HashSet<string> TracedModelTypes = new(StringComparer.Ordinal) { "qwen3_5", "qwen3_5_text" };

    private readonly IBaseCheckpointStore _checkpointStore;
    private readonly IRuntimeDeviceAudit _deviceAudit;
    private readonly GgufInspectionService _ggufInspection;

    public ModelLabCheckpointPreflightService(IBaseCheckpointStore checkpointStore,
        IRuntimeDeviceAudit deviceAudit,
        GgufInspectionService ggufInspection)
    {
        ArgumentNullException.ThrowIfNull(checkpointStore);
        ArgumentNullException.ThrowIfNull(deviceAudit);
        ArgumentNullException.ThrowIfNull(ggufInspection);
        _checkpointStore = checkpointStore;
        _deviceAudit = deviceAudit;
        _ggufInspection = ggufInspection;
    }

    public async Task<ModelLabCheckpointPreflight> CheckAsync(string repoId, string? revision, string installedModelName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoId);
        ArgumentException.ThrowIfNullOrWhiteSpace(installedModelName);

        BaseCheckpointManifest manifest;
        try
        {
            manifest = await _checkpointStore.ResolveAsync(repoId.Trim(), revision?.Trim(), ct);
        }
        catch (BaseCheckpointNotTrainableException exception)
        {
            return new ModelLabCheckpointPreflight
            {
                RepoId = repoId,
                Refusal = ModelLabCheckpointRefusal.NotTrainable,
                RefusalMessage = exception.Message
            };
        }

        var weightSizes = manifest.Files.Where(static file => file.Role == BaseCheckpointFileRole.Weights).Select(static file => file.SizeBytes).ToArray();
        return await CheckFitAndPairingAsync(manifest.RepoId,
            manifest.Revision,
            weightSizes,
            manifest.License,
            manifest.IsGated,
            metadataPresent: true,
            installedModelName.Trim(),
            token => ReadConfigOrNullAsync(manifest, token),
            ct);
    }

    /// <summary>The same checks for a checkpoint already on disk: no network, so a trace can start offline.</summary>
    public Task<ModelLabCheckpointPreflight> CheckDownloadedAsync(BaseArtifactView artifact, string installedModelName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentException.ThrowIfNullOrWhiteSpace(installedModelName);
        var weightSizes = artifact.Files.Where(static file => file.Role == nameof(BaseCheckpointFileRole.Weights)).Select(static file => file.SizeBytes).ToArray();
        var config = artifact.Files.FirstOrDefault(static file => string.Equals(file.FileName, ConfigFileName, StringComparison.OrdinalIgnoreCase));
        return CheckFitAndPairingAsync(artifact.RepoId,
            artifact.Revision,
            weightSizes,
            artifact.License?.License,
            artifact.License?.IsGated ?? false,
            metadataPresent: artifact.License is not null,
            installedModelName.Trim(),
            _ => Task.FromResult(config is null ? null : TrainingFootprintEstimator.ReadConfigOrNull(config.LocalPath)),
            ct);
    }

    private async Task<ModelLabCheckpointPreflight> CheckFitAndPairingAsync(string repoId,
        string revision,
        long[] weightSizes,
        string? license,
        bool isGated,
        bool metadataPresent,
        string installedModelName,
        Func<CancellationToken, Task<BaseCheckpointConfigV1?>> readConfig,
        CancellationToken ct)
    {
        var weightBytes = weightSizes.Sum();
        var requiredBytes = weightBytes + WorkingMemoryBytes;
        var profile = await _deviceAudit.GetEffectiveProfileAsync(forceRefreshProfile: false, ct);
        var cardBytes = profile is { VramKnown: true, VramBytes: > 0 } ? profile.VramBytes : null;
        var result = new ModelLabCheckpointPreflight
        {
            RepoId = repoId,
            Revision = revision,
            WeightBytes = weightBytes,
            RequiredVramBytes = requiredBytes,
            CardVramBytes = cardBytes,
            License = license,
            IsGated = isGated,
            // The same words the row records: a live hub answer always has metadata, a saved artifact may not.
            LicenseConfirmationText = LicenseGateService.BuildModelLabText(repoId, license, isGated, metadataPresent)
        };

        // An unsized shard counted as nothing would call any checkpoint a fit.
        if (Array.Exists(weightSizes, static size => size <= 0))
        {
            return Refuse(result with
            {
                WeightBytes = null,
                RequiredVramBytes = null
            }, ModelLabCheckpointRefusal.WeightSizeUnknown, $"Hugging Face did not report the size of every weight file of {repoId}, so its fit cannot be checked.");
        }

        if (cardBytes is not { } card)
        {
            return Refuse(result, ModelLabCheckpointRefusal.VramUnknown, "This node could not measure a GPU's memory, and Model Lab traces on the GPU only.");
        }

        if (requiredBytes > card)
        {
            return Refuse(result, ModelLabCheckpointRefusal.CannotFitCard,
                $"{repoId} cannot fit this card: tracing needs about {BaseArtifactService.FormatGigabytes(requiredBytes)} of GPU memory at full precision, and the card has {BaseArtifactService.FormatGigabytes(card)}.");
        }

        return await CheckPairingAsync(result, repoId, installedModelName, readConfig, ct);
    }

    private async Task<ModelLabCheckpointPreflight> CheckPairingAsync(ModelLabCheckpointPreflight result,
        string repoId,
        string installedModelName,
        Func<CancellationToken, Task<BaseCheckpointConfigV1?>> readConfig,
        CancellationToken ct)
    {
        GgufTensorInspection gguf;
        try
        {
            gguf = await _ggufInspection.InspectAsync(installedModelName, ct);
        }
        catch (InstalledGgufModelNotFoundException)
        {
            return Refuse(result, ModelLabCheckpointRefusal.GgufNotInstalled, $"'{installedModelName}' is not an installed GGUF model.");
        }

        var config = await readConfig(ct);
        var steerableBlocks = gguf.BlockCount - (gguf.NextnPredictLayers ?? 0);
        result = result with
        {
            CheckpointHiddenSize = config?.HiddenSize,
            CheckpointBlockCount = config?.NumHiddenLayers,
            GgufEmbeddingLength = gguf.EmbeddingLength,
            GgufBlockCount = gguf.BlockCount,
            GgufSteerableBlockCount = steerableBlocks,
            CheckpointLayerTypes = config?.LayerTypes,
            DefaultSteeringLayer = steerableBlocks >= 2 ? ModelLabTraceSpec.DefaultSteeringLayer((int)steerableBlocks.Value, config?.LayerTypes) : null
        };

        if (gguf.Failure != GgufTensorReadFailure.None || gguf.EmbeddingLength is null || gguf.BlockCount is null)
        {
            return Refuse(result, ModelLabCheckpointRefusal.GgufUnreadable, $"The header of '{installedModelName}' could not be read, so it cannot be paired.");
        }

        // The type first: another layout's config may name its shape differently, and the reason to give is the layout.
        if (config is not null && (config.ModelType is not { } modelType || !TracedModelTypes.Contains(modelType)))
        {
            return Refuse(result, ModelLabCheckpointRefusal.UnsupportedModelType,
                $"{repoId} is model type '{config.ModelType ?? "unknown"}', and Model Lab has only traced Qwen3.5 checkpoints: its hooks depend on that model's layout.");
        }

        if (config?.HiddenSize is not { } hiddenSize || config.NumHiddenLayers is not { } blockCount)
        {
            return Refuse(result, ModelLabCheckpointRefusal.ConfigUnreadable, $"The config.json of {repoId} could not be read or does not state a hidden size and a block count.");
        }

        if (hiddenSize != gguf.EmbeddingLength)
        {
            return Refuse(result, ModelLabCheckpointRefusal.EmbeddingMismatch,
                string.Create(CultureInfo.InvariantCulture, $"{repoId} has hidden size {hiddenSize} and '{installedModelName}' has embedding size {gguf.EmbeddingLength}, so a vector from one cannot apply to the other."));
        }

        if (blockCount == gguf.BlockCount)
        {
            return result;
        }

        var hint = gguf.BlockCount > blockCount ? " A GGUF exported with its MTP head counts that head as an extra block; use an export without it." : string.Empty;
        return Refuse(result, ModelLabCheckpointRefusal.BlockCountMismatch,
            string.Create(CultureInfo.InvariantCulture, $"{repoId} has {blockCount} blocks and '{installedModelName}' has {gguf.BlockCount}, so a layer from one does not name the same layer in the other.{hint}"));
    }

    private async Task<BaseCheckpointConfigV1?> ReadConfigOrNullAsync(BaseCheckpointManifest manifest, CancellationToken ct)
    {
        var configFile = manifest.Files.FirstOrDefault(static file => string.Equals(file.FileName, ConfigFileName, StringComparison.OrdinalIgnoreCase));
        if (configFile is not { SizeBytes: > 0 and <= MaxConfigBytes })
        {
            return null;
        }

        var directory = Directory.CreateTempSubdirectory("xe-model-lab-config-");
        try
        {
            var downloaded = await _checkpointStore.DownloadAsync(manifest with
                {
                    Files = [configFile],
                    TotalBytes = configFile.SizeBytes
                },
                directory.FullName,
                progress: null,
                ct);
            return TrainingFootprintEstimator.ReadConfigOrNull(downloaded.Files[0].LocalPath);
        }
        catch (HuggingFaceDownloadException)
        {
            // A gated repository without a token lands here too; the refusal says the config could not be read.
            return null;
        }
        finally
        {
            BestEffortFile.TryDeleteDirectory(directory.FullName);
        }
    }

    private static ModelLabCheckpointPreflight Refuse(ModelLabCheckpointPreflight result, ModelLabCheckpointRefusal refusal, string message) =>
        result with
        {
            Refusal = refusal,
            RefusalMessage = message
        };
}
