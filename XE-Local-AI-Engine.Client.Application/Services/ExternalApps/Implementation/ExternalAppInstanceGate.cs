namespace XE_Local_AI_Engine.Client.Services.ExternalApps.Implementation;

using System.Collections.Concurrent;
using System.Globalization;

/// <summary>One mutual-exclusion gate per instance, and one per application id for the already-installed check, held until the operation has settled the row.</summary>
/// <remarks>
///     It copies <c>IntegrationSessionGate</c> with one difference that is the whole contract: entering does not
///     QUEUE — a second command on a busy instance is answered "an operation is already in flight". That, and why
///     install takes both key shapes, is stated in <c>docs/wiki/23-external-apps.md</c> ("Lifecycle and restore
///     semantics").
/// </remarks>
// ponytail: a ConcurrentDictionary of semaphores, not a lock manager. Entries are dropped by Forget on uninstall;
// the map is bounded by the number of installed applications, which V1 caps at one instance each.
internal sealed class ExternalAppInstanceGate
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    /// <summary>Test seam: how many keys currently hold an entry, so <see cref="Forget" /> is provable.</summary>
    internal int TrackedCount => _gates.Count;

    /// <summary>The key one instance's operations serialise on.</summary>
    public static string InstanceKey(Guid instanceId)
    {
        return instanceId.ToString("N", CultureInfo.InvariantCulture);
    }

    /// <summary>The key install's already-installed check serialises on. Prefixed so it cannot collide with an instance key.</summary>
    public static string ApplicationKey(string applicationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);

        return "app:" + applicationId;
    }

    /// <summary>
    ///     Takes the key's gate if it is free, releasing it when the returned lease is disposed. Returns
    ///     <see langword="null" /> immediately when it is held; it never waits.
    /// </summary>
    public async Task<IDisposable?> TryEnterAsync(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var gate = _gates.GetOrAdd(key, static _ => new SemaphoreSlim(initialCount: 1, maxCount: 1));
        // Explicitly not propagating: a zero-timeout TryEnter never waits, so a token could not shorten it.
        return await gate.WaitAsync(TimeSpan.Zero, CancellationToken.None) ? new Lease(gate) : null;
    }

    /// <summary>Drops an uninstalled instance's entry; called from INSIDE the critical section.</summary>
    /// <remarks>
    ///     The semaphore is dropped rather than disposed: a caller already holding a reference to it keeps it, finds
    ///     no row on its own checks, and answers 404.
    /// </remarks>
    public void Forget(string key)
    {
        _ = _gates.TryRemove(key, out _);
    }

    private sealed class Lease : IDisposable
    {
        private SemaphoreSlim? _gate;

        public Lease(SemaphoreSlim gate)
        {
            _gate = gate;
        }

        public void Dispose()
        {
            // Idempotent: releasing twice would hand one gate to two holders at once.
            var released = Interlocked.Exchange(ref _gate, value: null);
            _ = released?.Release();
        }
    }
}
