namespace XE_Local_AI_Engine.Client.Services.ModelLab;

using System.Globalization;
using System.Text.Json;
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
    /// <summary>What tracing needs beyond the weights: the process, the activations and the capped trace buffers. Recorded attention fits inside it: its kept rows are a few megabytes, and its float32 key copy at 8,192 tokens measured about 17 MiB.</summary>
    internal const long WorkingMemoryBytes = 2L * 1024 * 1024 * 1024;

    private const string ConfigFileName = "config.json";

    private const long MaxConfigBytes = 1024 * 1024;

    /// <summary><c>Qwen3_5TextConfig</c>'s default <c>full_attention_interval</c>.</summary>
    private const int DefaultFullAttentionInterval = 4;

    /// <summary>A bound on a list built from a downloaded number, far past any model this card can trace.</summary>
    private const int MaxDerivedBlocks = 1024;

    private const string FullAttention = "full_attention";

    private const string Olmoe = "olmoe";

    /// <summary>
    ///     What <c>modellab.py</c> has traced, each with the <c>general.architecture</c> llama.cpp writes for its GGUF. Its hooks need <c>model.model</c>'s
    ///     blocks, which not every layout has; extend this after tracing one.
    /// </summary>
    private static readonly Dictionary<string, string> TracedModelTypes = new(StringComparer.Ordinal)
    {
        ["qwen3_5"] = "qwen35",
        ["qwen3_5_text"] = "qwen35",
        [Olmoe] = Olmoe
    };

    /// <summary><c>modellablib.ATTENTION_FAMILIES</c>: families whose attention source was read and whose recomputed rows were checked against an eager pass.</summary>
    /// <remarks>The recompute is plain causal softmax(q·kᵀ·scaling), so a family joins only with that proof.</remarks>
    private static readonly HashSet<string> AttentionModelTypes = new(StringComparer.Ordinal)
    {
        "qwen3_5",
        "qwen3_5_text",
        Olmoe
    };

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
            MetadataPresent = metadataPresent,
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
        var steerableBlocks = gguf.SteerableBlockCount;
        var layerTypes = LayerTypesOrNull(config);
        var attentionRefusal = AttentionRefusalOrNull(config, layerTypes);
        // Only a family whose forward has routers collects router logits, with capture hooks a trace cannot remove.
        var routerLogits = config is { ModelType: Olmoe, OutputRouterLogits: true };
        result = result with
        {
            CheckpointHiddenSize = config?.HiddenSize,
            CheckpointBlockCount = config?.NumHiddenLayers,
            GgufEmbeddingLength = gguf.EmbeddingLength,
            GgufBlockCount = gguf.BlockCount,
            GgufSteerableBlockCount = steerableBlocks,
            ModelType = config?.ModelType,
            CheckpointLayerTypes = layerTypes,
            CheckpointAttentionHeads = config?.NumAttentionHeads,
            AttentionRefusal = attentionRefusal?.Reason,
            AttentionRefusalMessage = attentionRefusal?.Message,
            // modellab.trace refuses it too, but only after the weights are on the GPU.
            TraceRefusal = routerLogits ? ModelLabTraceRefusal.RouterLogitsRequested : null,
            TraceRefusalMessage = routerLogits
                ? "This checkpoint's config sets output_router_logits, which a trace does not support. It can still be steered."
                : null,
            DefaultSteeringLayer = steerableBlocks >= 2 ? ModelLabTraceSpec.DefaultSteeringLayer((int)steerableBlocks.Value, layerTypes) : null
        };

        if (gguf.Failure != GgufTensorReadFailure.None || gguf.EmbeddingLength is null || gguf.BlockCount is null)
        {
            return Refuse(result, ModelLabCheckpointRefusal.GgufUnreadable, $"The header of '{installedModelName}' could not be read, so it cannot be paired.");
        }

        // The type first: another layout's config may name its shape differently, and the reason to give is the layout.
        var architecture = config?.ModelType is { } modelType ? TracedModelTypes.GetValueOrDefault(modelType) : null;
        if (config is not null && architecture is null)
        {
            return Refuse(result, ModelLabCheckpointRefusal.UnsupportedModelType,
                $"{repoId} is model type '{config.ModelType ?? "unknown"}', and Model Lab has only traced Qwen3.5 and OLMoE checkpoints: its hooks depend on those models' layouts.");
        }

        // Equal width and depth do not make two families' residual streams the same space.
        if (config is not null && gguf.Architecture != architecture)
        {
            return Refuse(result, ModelLabCheckpointRefusal.ArchitectureMismatch,
                $"{repoId} is model type '{config.ModelType}', whose GGUF architecture is '{architecture}', and '{installedModelName}' is '{gguf.Architecture ?? "unknown"}', so a vector from one does not describe the other.");
        }

        if (config?.HiddenSize is not { } hiddenSize || config.NumHiddenLayers is not { } blockCount)
        {
            return Refuse(result, ModelLabCheckpointRefusal.ConfigUnreadable, $"The config.json of {repoId} could not be read or does not state a hidden size and a block count.");
        }

        if (hiddenSize != gguf.EmbeddingLength)
        {
            return Refuse(result, ModelLabCheckpointRefusal.EmbeddingMismatch,
                string.Create(CultureInfo.InvariantCulture,
                    $"{repoId} has hidden size {hiddenSize} and '{installedModelName}' has embedding size {gguf.EmbeddingLength}, so a vector from one cannot apply to the other."));
        }

        if (blockCount == gguf.BlockCount)
        {
            return result;
        }

        var hint = gguf.BlockCount > blockCount ? " A GGUF exported with its MTP head counts that head as an extra block; use an export without it." : string.Empty;
        return Refuse(result, ModelLabCheckpointRefusal.BlockCountMismatch,
            string.Create(CultureInfo.InvariantCulture,
                $"{repoId} has {blockCount} blocks and '{installedModelName}' has {gguf.BlockCount}, so a layer from one does not name the same layer in the other.{hint}"));
    }

    /// <summary>Each block's kind by the traced family's rule, as <c>modellablib.layer_kind</c> reads it: OLMoE all full whatever the config states; Qwen3.5 its own list, else its interval.</summary>
    /// <remarks>transformers' interval rule: block i is full attention when (i + 1) is a multiple of the interval, 4 by default. Another family's blocks are not guessed.</remarks>
    private static IReadOnlyList<string>? LayerTypesOrNull(BaseCheckpointConfigV1? config)
    {
        if (config is not { ModelType: { } modelType } || !TracedModelTypes.ContainsKey(modelType))
        {
            return null;
        }

        if (modelType != Olmoe && config.LayerTypes is { } stated)
        {
            return stated;
        }

        if (config is not { NumHiddenLayers: int blocks and > 0 and <= MaxDerivedBlocks })
        {
            return null;
        }

        if (modelType == Olmoe)
        {
            return [.. Enumerable.Repeat(FullAttention, blocks)];
        }

        if (config.FullAttentionInterval is <= 0)
        {
            return null;
        }

        var interval = config.FullAttentionInterval ?? DefaultFullAttentionInterval;
        return [.. Enumerable.Range(0, blocks).Select(layer => (layer + 1) % interval == 0 ? FullAttention : "linear_attention")];
    }

    /// <summary>Why attention cannot be recorded for this config, or null: <c>modellab.trace</c>'s rule, checking the family, then an OLMoE config's contradictions, then the shape.</summary>
    /// <remarks>
    ///     OLMoE's kinds are all full by family rule, so a stated list that is not full attention at every block, or a window, chunk or shared-cache key,
    ///     means the config describes something the recompute would not match.
    /// </remarks>
    internal static AttentionRefusalText? AttentionRefusalOrNull(BaseCheckpointConfigV1? config, IReadOnlyList<string>? layerTypes)
    {
        if (config?.ModelType is not { } modelType || !AttentionModelTypes.Contains(modelType))
        {
            return new AttentionRefusalText(ModelLabAttentionRefusal.UnsupportedModelType, "Attention capture is implemented for Qwen3.5 and OLMoE checkpoints; start the trace without it.");
        }

        if (modelType == Olmoe
            && ((config.LayerTypes is { } stated && (stated.Count != config.NumHiddenLayers || stated.Any(static kind => kind != FullAttention)))
                || IsSet(config.SlidingWindow)
                || IsSet(config.AttentionChunkSize)
                || IsSet(config.NumKvSharedLayers)))
        {
            return new AttentionRefusalText(ModelLabAttentionRefusal.NotFullAttention,
                "This OLMoE checkpoint's config describes attention other than full attention at every block (its layer_types, sliding_window, attention_chunk_size or "
                + "num_kv_shared_layers), and attention is recorded only for full attention. Start the trace without it.");
        }

        if (layerTypes is null || config.NumAttentionHeads is not { } heads)
        {
            return new AttentionRefusalText(ModelLabAttentionRefusal.ShapeNotStated,
                "This checkpoint's config does not state its block kinds and attention heads, so attention cannot be recorded. Start the trace without it.");
        }

        if (ModelLabTraceSpec.AttentionLayersFor(layerTypes).Count == 0)
        {
            return new AttentionRefusalText(ModelLabAttentionRefusal.NoFullAttentionBlock,
                "This checkpoint has no full-attention block, and only those have attention weights to record. Start the trace without it.");
        }

        return heads is < 1 or > ModelLabTraceSpec.MaxAttentionHeads
            ? new AttentionRefusalText(ModelLabAttentionRefusal.HeadCountOutOfRange, string.Create(CultureInfo.InvariantCulture,
                $"This checkpoint has {heads} attention heads per block, and a recording holds 1 to {ModelLabTraceSpec.MaxAttentionHeads}. Start the trace without it."))
            : null;

        static bool IsSet(JsonElement? value) =>
            value is { ValueKind: not JsonValueKind.Null };
    }

    /// <summary>An attention refusal's stable code and its English text.</summary>
    internal readonly record struct AttentionRefusalText(ModelLabAttentionRefusal Reason, string Message);

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
