namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;

using System.Collections.Concurrent;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

/// <summary>
///     Default <see cref="IImageServerProgressBroker" />: a per-model fan-out of parsed stdout observations from the
///     launcher's drain threads to whichever generation is currently listening. Singleton — the launcher publishes for
///     the lifetime of every child process it started, while subscriptions come and go with each generation.
/// </summary>
/// <remarks>
///     A publish never blocks and never throws: it runs on the thread that drains the child's stdout pipe, so a slow or
///     faulting handler there would back the pipe up and eventually stall the daemon itself. Handlers are therefore
///     invoked inline but wrapped, and an observation with no subscriber is simply dropped.
/// </remarks>
internal sealed class ImageServerProgressBroker : IImageServerProgressBroker
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Action<SdProgressObservation>>> _subscribers =
        new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public void Publish(string modelName, SdProgressObservation observation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        ArgumentNullException.ThrowIfNull(observation);

        if (!_subscribers.TryGetValue(modelName, out var handlers))
        {
            return;
        }

        foreach (var handler in handlers.Values)
        {
            try
            {
                handler(observation);
            }
            catch (Exception)
            {
                // Progress is strictly best-effort: a faulting consumer must never propagate onto the stdout drain
                // loop, whose only real job is keeping the child's pipe from filling.
            }
        }
    }

    /// <inheritdoc />
    public IDisposable Subscribe(string modelName, Action<SdProgressObservation> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        ArgumentNullException.ThrowIfNull(handler);

        var handlers = _subscribers.GetOrAdd(modelName, static _ => new ConcurrentDictionary<Guid, Action<SdProgressObservation>>());
        var token = Guid.NewGuid();
        handlers[token] = handler;
        return new Subscription(this, modelName, token);
    }

    private void Unsubscribe(string modelName, Guid token)
    {
        if (!_subscribers.TryGetValue(modelName, out var handlers))
        {
            return;
        }

        _ = handlers.TryRemove(token, out _);

        // Drop the now-empty per-model bucket so a node that installs and generates with many models does not
        // accumulate one dictionary per model name for the process lifetime. A racing Subscribe re-adds it via
        // GetOrAdd; the worst case is one observation delivered to nobody, which is already the no-subscriber path.
        if (handlers.IsEmpty)
        {
            _ = _subscribers.TryRemove(new KeyValuePair<string, ConcurrentDictionary<Guid, Action<SdProgressObservation>>>(modelName, handlers));
        }
    }

    /// <summary>The generation epoch handle. Idempotent: a double dispose (finally plus an outer using) unsubscribes once.</summary>
    private sealed class Subscription(ImageServerProgressBroker broker, string modelName, Guid token) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, value: 1) == 0)
            {
                broker.Unsubscribe(modelName, token);
            }
        }
    }
}
