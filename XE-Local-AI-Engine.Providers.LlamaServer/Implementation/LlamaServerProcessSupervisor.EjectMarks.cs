namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

/// <summary>
///     Operator-eject half of <see cref="LlamaServerProcessSupervisor" />: the per-key mark an operator eject leaves,
///     which refuses a Background cold launch and vetoes the publication of a load no user waited on since the eject.
/// </summary>
/// <remarks>
///     Every read and write sits under <see cref="_ejectMarkLock" />, never the per-key ensure gate: profiling holds
///     that gate while awaiting detached spawns, which publish under this lock. The marks live in memory only, so a
///     restart starts unmarked.
/// </remarks>
public sealed partial class LlamaServerProcessSupervisor
{
    private const string OperatorEjectedMessage = "The model was ejected by the operator; it reloads when it is next used directly.";

    private readonly Lock _ejectMarkLock = new();
    private readonly Dictionary<ProcessKey, EjectMark> _ejectMarks = new();

    /// <summary>How long a vetoed child's teardown waits to see it exit before the reaper holds it; settable so a test can make it immediate.</summary>
    internal TimeSpan VetoedKillExitWait { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Awaited by an eject right after it opened its attempt and read the table. Test seam for publication races; null in production.</summary>
    internal Func<Task>? OperatorEjectAttemptOpenedForTesting { get; set; }

    /// <summary>
    ///     Opens an operator eject attempt for <paramref name="key" /> and returns its generation token, with the process
    ///     registered at that instant as the eject's only <paramref name="target" />.
    /// </summary>
    /// <remarks>
    ///     The table is read under the same lock publication writes it under, so a load a user-waited caller published
    ///     after the generation opened is never torn down by this eject.
    /// </remarks>
    private long BeginOperatorEjectAttempt(ProcessKey key, out RunningProcess? target)
    {
        lock (_ejectMarkLock)
        {
            var mark = GetOrAddEjectMark(key);
            var token = ++mark.Generation;
            mark.ActiveAttempts.Add(token);
            _processes.TryGetValue(key, out target);
            return token;
        }
    }

    /// <summary>Closes an attempt; only an eject that completed raises the mark, so a failed one rolls back nothing but itself.</summary>
    private void EndOperatorEjectAttempt(ProcessKey key, long token, bool marks)
    {
        lock (_ejectMarkLock)
        {
            var mark = _ejectMarks[key];
            if (marks)
            {
                mark.MarkedThrough = Math.Max(mark.MarkedThrough, token);
            }

            mark.ActiveAttempts.Remove(token);
        }
    }

    /// <summary>
    ///     The decision-time check for a NEW launch: refuses a Background launch of a blocked key, and returns the
    ///     generation a user-waited launch qualifies at, or <see langword="null" /> for a Background one.
    /// </summary>
    private long? QualifyNewLaunch(ProcessKey key, ModelResidencyIntent intent)
    {
        bool firstForGeneration;
        lock (_ejectMarkLock)
        {
            _ejectMarks.TryGetValue(key, out var mark);
            if (intent != ModelResidencyIntent.Background)
            {
                return mark?.Generation ?? 0;
            }

            if (mark is not { IsBlocked: true })
            {
                return null;
            }

            firstForGeneration = mark.TakeRefusalLog();
        }

        LogOperatorEjectRefusal(key, firstForGeneration, "Background load");
        throw new OperatorEjectedException(OperatorEjectedMessage);
    }

    /// <summary>Joins an in-flight spawn unless publication already refused it; a user-waited joiner qualifies it at the current generation.</summary>
    private bool TryJoinInflightSpawn(ProcessKey key, InflightSpawn inflight, ModelResidencyIntent intent)
    {
        lock (_ejectMarkLock)
        {
            if (inflight.IsRefused)
            {
                return false;
            }

            if (intent != ModelResidencyIntent.Background)
            {
                var generation = _ejectMarks.TryGetValue(key, out var mark) ? mark.Generation : 0;
                inflight.QualifiedAtGeneration = Math.Max(inflight.QualifiedAtGeneration ?? 0, generation);
            }
        }

        if (intent != ModelResidencyIntent.Transient)
        {
            inflight.ClearTransient();
        }

        return true;
    }

    /// <summary>
    ///     Publishes <paramref name="running" /> as one decision under the lock, or refuses the spawn, which then
    ///     accepts no joins and throws <see cref="OperatorEjectedException" /> for its caller to clean up.
    /// </summary>
    /// <remarks>
    ///     Allowed when the key is not blocked, or a user-waited caller qualified the spawn at or after the latest eject
    ///     attempt; that caller's generation then clears the mark, never a newer one. A spawn with no in-flight record
    ///     is never read as unqualified.
    /// </remarks>
    private void PublishOrVeto(ProcessKey key, RunningProcess running, InflightSpawn? inflight)
    {
        bool firstForGeneration;
        lock (_ejectMarkLock)
        {
            _ejectMarks.TryGetValue(key, out var mark);
            var qualified = inflight is null ? long.MaxValue : inflight.QualifiedAtGeneration;
            if (mark is null || !mark.IsBlocked || qualified >= mark.Generation)
            {
                if (mark is not null && inflight?.QualifiedAtGeneration is { } clearedAt)
                {
                    mark.ClearedThrough = Math.Max(mark.ClearedThrough, clearedAt);
                }

                _processes[key] = running;
                return;
            }

            inflight!.IsRefused = true;
            firstForGeneration = mark.TakeRefusalLog();
        }

        LogOperatorEjectRefusal(key, firstForGeneration, "Finished load");
        throw new OperatorEjectedException(OperatorEjectedMessage);
    }

    /// <summary>Waits out a refused in-flight spawn's cleanup so the caller can decide afresh; its fault is the refused callers' to see.</summary>
    /// <remarks>A cancelled caller gets its own cancellation, never the refusal of a spawn it did not join.</remarks>
    internal static async Task WaitForRefusedSpawnAsync(Task refusedSpawn, CancellationToken ct)
    {
        try
        {
            await refusedSpawn.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The refusal belongs to the callers of that spawn; this caller re-runs its own decision.
        }

        ct.ThrowIfCancellationRequested();
    }

    /// <summary>The in-flight spawn's qualifying generation for a key, or <see langword="null" />. Test seam for join assertions.</summary>
    internal long? GetInflightQualifiedAtGeneration(string modelName, ModelRole role)
    {
        lock (_ejectMarkLock)
        {
            return _inflightSpawns.TryGetValue(new ProcessKey(modelName, role), out var inflight) ? inflight.QualifiedAtGeneration : null;
        }
    }

    private void LogOperatorEjectRefusal(ProcessKey key, bool firstForGeneration, string what)
    {
        _logger.Log(firstForGeneration ? LogLevel.Information : LogLevel.Debug,
            "{What} of model {ModelName} role {Role} skipped: the operator ejected it, and only a user request reloads it.",
            what, key.ModelName, key.Role);
    }

    private EjectMark GetOrAddEjectMark(ProcessKey key)
    {
        if (!_ejectMarks.TryGetValue(key, out var mark))
        {
            mark = new EjectMark();
            _ejectMarks.Add(key, mark);
        }

        return mark;
    }

    /// <summary>One key's eject generations, read and written only under <see cref="_ejectMarkLock" />.</summary>
    private sealed class EjectMark
    {
        private long _loggedGeneration;

        /// <summary>Raised at the start of every operator eject attempt.</summary>
        public long Generation { get; set; }

        /// <summary>The attempts still draining or tearing down.</summary>
        public HashSet<long> ActiveAttempts { get; } = [];

        /// <summary>The highest generation whose eject completed.</summary>
        public long MarkedThrough { get; set; }

        /// <summary>The highest generation a user-waited publish has cleared.</summary>
        public long ClearedThrough { get; set; }

        public bool IsBlocked => ActiveAttempts.Count > 0 || MarkedThrough > ClearedThrough;

        /// <summary>True for the first refusal under the current generation, which is logged at Information; later ones log at Debug.</summary>
        public bool TakeRefusalLog()
        {
            if (_loggedGeneration == Generation)
            {
                return false;
            }

            _loggedGeneration = Generation;
            return true;
        }
    }
}
