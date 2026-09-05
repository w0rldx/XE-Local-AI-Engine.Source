namespace XE_Local_AI_Engine.Client.Common.Telemetry;

using System.Collections.Concurrent;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;

/// <summary>
///     Bridges provider load observations to the application meter, and remembers the VRAM figures of the most recent
///     SUCCESSFUL load per <c>(model, role)</c> so a later reader — the dev-workflow cost collector — can say what the
///     box looked like when the model that served it was loaded. Runtime identity and the model name deliberately stay
///     off metric tags; both change freely and would create unbounded cardinality.
/// </summary>
/// <remarks>
///     Process-lifetime singleton. The remembered figures are report-only and deliberately not persisted: they describe
///     one load of one process, and a value carried across a restart would describe a process that no longer exists.
///     Only a <see cref="LlamaServerReadinessOutcome.Ready" /> load writes an entry — a failed or cancelled attempt
///     never became the model that served anything, and overwriting a good reading with its numbers would misattribute
///     them. Entries are keyed like the layer-placement report and bounded the same way: by the set of installed models.
/// </remarks>
internal sealed class NodeMetricsLlamaServerLoadTelemetry : ILlamaServerLoadTelemetry
{
    private readonly ConcurrentDictionary<LoadKey, LlamaServerVramAtLoad> _lastReadyLoads = new();

    /// <summary>
    ///     The VRAM figures of the most recent successful load of this <c>(model, role)</c>, or <see langword="null" />
    ///     when no such load has been observed in this process — a remote or Ollama model, a model already resident
    ///     before the node started, or a node that has not loaded it at all.
    /// </summary>
    public LlamaServerVramAtLoad? TryGetLastReadyLoad(string modelName, ModelRole role)
    {
        return string.IsNullOrWhiteSpace(modelName)
            ? null
            : _lastReadyLoads.GetValueOrDefault(new LoadKey(modelName, role));
    }

    public void RecordLoad(LlamaServerLoadObservation observation)
    {
        if (observation.Outcome == LlamaServerReadinessOutcome.Ready
            && !string.IsNullOrWhiteSpace(observation.ModelName)
            && (observation.GlobalFreeVramBytesAtLoad is not null || observation.AdmittedVramBytes is not null))
        {
            _lastReadyLoads[new LoadKey(observation.ModelName, observation.Role)] =
                new LlamaServerVramAtLoad(observation.GlobalFreeVramBytesAtLoad, observation.AdmittedVramBytes);
        }

        var role = Role(observation.Role);
        var variant = Variant(observation.Variant);
        var outcome = Outcome(observation.Outcome);

        NodeMetrics.LlamaServerLoadReadinessDurationMs.Record(observation.ReadinessDurationMs,
            new KeyValuePair<string, object?>("role", role),
            new KeyValuePair<string, object?>("variant", variant),
            new KeyValuePair<string, object?>("outcome", outcome));
        NodeMetrics.LlamaServerLoadTotal.Add(1,
            new KeyValuePair<string, object?>("role", role),
            new KeyValuePair<string, object?>("variant", variant),
            new KeyValuePair<string, object?>("outcome", outcome),
            new KeyValuePair<string, object?>("placement", Placement(observation.Placement)),
            new KeyValuePair<string, object?>("attempt", Attempt(observation.AttemptKind)),
            new KeyValuePair<string, object?>("speculation", Speculation(observation.SpeculativeModeClass)));
    }

    private static string Role(ModelRole role)
    {
        return role switch
        {
            ModelRole.Embedding => "embedding",
            ModelRole.Reranker => "reranker",
            _ => "chat"
        };
    }

    private static string Variant(GpuVariant variant)
    {
        return variant switch
        {
            GpuVariant.Cuda => "cuda",
            GpuVariant.Vulkan => "vulkan",
            _ => "cpu"
        };
    }

    private static string Outcome(LlamaServerReadinessOutcome outcome)
    {
        return outcome switch
        {
            LlamaServerReadinessOutcome.Ready => "ready",
            LlamaServerReadinessOutcome.Cancelled => "cancelled",
            _ => "failed"
        };
    }

    private static string Placement(LlamaServerPlacementOutcome placement)
    {
        return placement switch
        {
            LlamaServerPlacementOutcome.Cpu => "cpu",
            LlamaServerPlacementOutcome.Full => "full",
            LlamaServerPlacementOutcome.Partial => "partial",
            LlamaServerPlacementOutcome.None => "none",
            _ => "unknown"
        };
    }

    private static string Attempt(LlamaServerLoadAttemptKind attemptKind)
    {
        return attemptKind == LlamaServerLoadAttemptKind.SafeRetry ? "safe_retry" : "primary";
    }

    private static string Speculation(SpeculativeModeClass modeClass)
    {
        return modeClass switch
        {
            SpeculativeModeClass.ExternalDraft => "external_draft",
            SpeculativeModeClass.MainModelHeads => "main_model_heads",
            SpeculativeModeClass.Draftless => "draftless",
            _ => "none"
        };
    }

    // Model names are compared the way every other (model, role) key in the runtime compares them. The role member is
    // NOT called Role: that would shadow this class's own Role(ModelRole) tag helper (S3218).
    private readonly record struct LoadKey(string ModelName, ModelRole LoadRole)
    {
        public bool Equals(LoadKey other) =>
            LoadRole == other.LoadRole && string.Equals(ModelName, other.ModelName, StringComparison.OrdinalIgnoreCase);

        public override int GetHashCode() =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(ModelName), LoadRole);
    }
}

/// <summary>
///     What the box looked like when a model was last loaded successfully: the machine-global free VRAM the capacity
///     gate measured just before admitting the load, and the GPU bytes it reserved for that process. Either half can be
///     <see langword="null" /> on its own — a CPU-only or non-NVIDIA host has no global-free figure to read.
/// </summary>
internal sealed record LlamaServerVramAtLoad(long? GlobalFreeVramBytesAtLoad, long? AdmittedVramBytes);
