namespace XE_Local_AI_Engine.Client.Common.Telemetry;

using System.Collections.Concurrent;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Process-lifetime record of local inference speed: the last ready load and last unload reason per <c>(model, role)</c>, and the
///     throughput of the most recent chat turns. Numbers, model names and timestamps only; never prompt or answer text.
/// </summary>
/// <remarks>
///     Read by <c>LocalRuntimeWarmer</c> (why a cold load happens, how long the last one took) and by the support bundle's
///     <c>perf.json</c>. Never persisted. Load entries are bounded by the installed models, turns by <see cref="MaxTurns" />.
/// </remarks>
public sealed class LocalModelPerfLog
{
    public const int MaxTurns = 20;

    private readonly ConcurrentDictionary<LoadKey, LocalModelLoadHistory> _loads = new();
    private readonly Queue<LocalModelTurnPerf> _turns = new();
    private readonly Lock _turnsGate = new();
    private readonly TimeProvider _timeProvider;

    public LocalModelPerfLog(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
    }

    /// <summary>A ready load replaces the key's history, so a later unload reason always describes THIS process.</summary>
    public void RecordReadyLoad(string modelName, ModelRole role, double readinessDurationMs)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return;
        }

        _loads[new LoadKey(modelName, role)] = new LocalModelLoadHistory
        {
            ModelName = modelName,
            Role = role,
            LastReadyLoadMs = readinessDurationMs,
            LastReadyAtUtc = _timeProvider.GetUtcNow()
        };
    }

    public void RecordUnload(string modelName, ModelRole role, LlamaServerUnloadReason reason)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return;
        }

        _ = _loads.AddOrUpdate(new LoadKey(modelName, role),
            _ => new LocalModelLoadHistory
            {
                ModelName = modelName,
                Role = role,
                LastUnloadReason = reason
            },
            (_, existing) => existing with
            {
                LastUnloadReason = reason
            });
    }

    /// <summary>The key's history, or <see langword="null" /> when this process never loaded nor unloaded it.</summary>
    public LocalModelLoadHistory? TryGetLoadHistory(string modelName, ModelRole role)
    {
        return string.IsNullOrWhiteSpace(modelName) ? null : _loads.GetValueOrDefault(new LoadKey(modelName, role));
    }

    /// <summary>Keeps the newest <see cref="MaxTurns" /> turns; a turn without throughput facts is not recorded.</summary>
    public void RecordTurn(string? modelName, InvocationThroughput? throughput)
    {
        if (throughput is null || throughput.IsEmpty)
        {
            return;
        }

        var turn = new LocalModelTurnPerf
        {
            AtUtc = _timeProvider.GetUtcNow(),
            Model = modelName,
            TimeToFirstTokenMs = throughput.TimeToFirstTokenMs,
            PromptTokens = throughput.PromptTokens,
            PromptMs = throughput.PromptMs,
            GenerationTokens = throughput.GenerationTokens,
            GenerationMs = throughput.GenerationMs,
            CachedPromptTokens = throughput.CachedPromptTokens
        };
        lock (_turnsGate)
        {
            _turns.Enqueue(turn);
            while (_turns.Count > MaxTurns)
            {
                _ = _turns.Dequeue();
            }
        }
    }

    /// <summary>Turns oldest first, loads ordered by model then role.</summary>
    public LocalModelPerfSnapshot Snapshot()
    {
        LocalModelTurnPerf[] turns;
        lock (_turnsGate)
        {
            turns = [.. _turns];
        }

        return new LocalModelPerfSnapshot
        {
            Turns = turns,
            Loads = [.. _loads.Values.OrderBy(static load => load.ModelName, StringComparer.OrdinalIgnoreCase).ThenBy(static load => load.Role)]
        };
    }

    // Model names compare case-insensitively, like every other (model, role) key in the runtime.
    private readonly record struct LoadKey(string ModelName, ModelRole LoadRole)
    {
        public bool Equals(LoadKey other) =>
            LoadRole == other.LoadRole && string.Equals(ModelName, other.ModelName, StringComparison.OrdinalIgnoreCase);

        public override int GetHashCode() =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(ModelName), LoadRole);
    }
}
