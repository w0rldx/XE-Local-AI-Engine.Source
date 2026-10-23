namespace XE_Local_AI_Engine.Client.Common.Telemetry;

using System.Text.Json.Serialization;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>The last ready load and the last deliberate unload of one <c>(model, role)</c>, as <see cref="LocalModelPerfLog" /> saw them.</summary>
public sealed record LocalModelLoadHistory
{
    public required string ModelName { get; init; }

    [JsonConverter(typeof(JsonStringEnumConverter<ModelRole>))]
    public required ModelRole Role { get; init; }

    /// <summary>Readiness duration of the last ready load; null when only an unload was seen.</summary>
    public double? LastReadyLoadMs { get; init; }

    public DateTimeOffset? LastReadyAtUtc { get; init; }

    /// <summary>Why the process of the last ready load was torn down; null while it is still loaded or was lost another way.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<LlamaServerUnloadReason>))]
    public LlamaServerUnloadReason? LastUnloadReason { get; init; }
}

/// <summary>One completed chat turn's throughput facts (see <see cref="InvocationThroughput" />), its model and when it finished.</summary>
public sealed record LocalModelTurnPerf
{
    public required DateTimeOffset AtUtc { get; init; }

    public string? Model { get; init; }

    public double? TimeToFirstTokenMs { get; init; }

    public int? PromptTokens { get; init; }

    public double? PromptMs { get; init; }

    public int? GenerationTokens { get; init; }

    public double? GenerationMs { get; init; }

    public int? CachedPromptTokens { get; init; }
}

/// <summary>What the support bundle writes as <c>perf.json</c>.</summary>
public sealed record LocalModelPerfSnapshot
{
    public required IReadOnlyList<LocalModelTurnPerf> Turns { get; init; }

    public required IReadOnlyList<LocalModelLoadHistory> Loads { get; init; }
}
