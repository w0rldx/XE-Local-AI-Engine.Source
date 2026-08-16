namespace XE_Local_AI_Engine.Client.Common.Telemetry;

using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;

/// <summary>
///     Bridges provider load observations to the application meter. Runtime identity deliberately stays off metric tags;
///     it can change frequently for custom builds and would create unbounded cardinality.
/// </summary>
internal sealed class NodeMetricsLlamaServerLoadTelemetry : ILlamaServerLoadTelemetry
{
    public void RecordLoad(LlamaServerLoadObservation observation)
    {
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
}
