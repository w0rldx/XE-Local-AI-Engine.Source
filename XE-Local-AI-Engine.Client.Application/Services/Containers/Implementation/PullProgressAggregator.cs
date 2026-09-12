namespace XE_Local_AI_Engine.Client.Services.Containers.Implementation;

using System.Runtime.InteropServices;
using Docker.DotNet.Models;

/// <summary>
///     Turns the daemon's per-layer pull stream into one progress figure a user can read.
///     <para>
///         Two things make this more than a projection. The daemon emits hundreds of messages a second across every
///         layer at once, and a hub that relayed each one would spend the pull serialising status text — so emission
///         is throttled, with a final report so the last state is never the one that was dropped. And the daemon
///         reports a byte total for some layers and not others, so the totals are best-effort: a zero
///         <see cref="ContainerPullProgress.TotalBytes" /> means "the daemon did not say" and the layer counts are
///         what a caller renders instead of a false percentage.
///     </para>
/// </summary>
internal sealed class PullProgressAggregator
{
    /// <summary>The shortest gap between two emitted reports.</summary>
    internal static readonly TimeSpan EmissionInterval = TimeSpan.FromMilliseconds(500);

    private readonly Dictionary<string, LayerProgress> _layers = new(StringComparer.Ordinal);
    private readonly string _imageReference;
    private readonly TimeProvider _timeProvider;
    private DateTimeOffset _lastEmission = DateTimeOffset.MinValue;

    public PullProgressAggregator(string imageReference, TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageReference);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _imageReference = imageReference;
        _timeProvider = timeProvider;
    }

    /// <summary>
    ///     The first error the daemon reported in the stream, or null. A pull that fails part-way still completes the
    ///     HTTP call normally, so this is the only place the failure is visible — reading it after the call is what
    ///     turns a silent half-pull into a named failure.
    /// </summary>
    public string? Error { get; private set; }

    /// <summary>
    ///     Fold one daemon message in, and return a report when one is due. Null means "nothing to emit yet", which is
    ///     the common case: the throttle exists because the caller relays every report over a hub.
    /// </summary>
    public ContainerPullProgress? Report(JSONMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        // JSONMessage.Error is a JSONError object rather than a string, so the text is on its Message. Reading the
        // object itself would render a type name into an operator-facing failure.
        if (message.Error is { } error && Error is null)
        {
            Error = string.IsNullOrWhiteSpace(error.Message) ? "The image pull failed." : error.Message;
        }

        Fold(message);

        var now = _timeProvider.GetUtcNow();
        if (now - _lastEmission < EmissionInterval)
        {
            return null;
        }

        _lastEmission = now;
        return Snapshot();
    }

    /// <summary>The last state, emitted unconditionally so a completed pull never ends on a throttled report.</summary>
    public ContainerPullProgress Snapshot()
    {
        var completed = 0;
        long current = 0;
        long total = 0;

        foreach (var layer in _layers.Values)
        {
            if (layer.Complete)
            {
                completed++;
            }

            current += layer.Current;
            total += layer.Total;
        }

        return new ContainerPullProgress
        {
            ImageReference = _imageReference,
            LayerCount = _layers.Count,
            CompletedLayers = completed,
            CurrentBytes = current,
            TotalBytes = total
        };
    }

    private void Fold(JSONMessage message)
    {
        // Messages without an id are the pull's own narration — the closing digest line and the up-to-date line.
        // Per-layer messages carry one, and the layer id is an opaque daemon digest that identifies nothing about
        // the user.
        if (string.IsNullOrEmpty(message.ID) || string.IsNullOrEmpty(message.Status))
        {
            return;
        }

        // One narration line does carry an id and is not a layer. The daemon opens every pull with a line whose
        // status begins as below and whose id is the tag or digest that was asked for. Folding it in would add a
        // layer that never completes, so a finished pull would report n of n plus one layers forever — which is
        // exactly what ContainerRuntimeRealDaemonTests caught against a real daemon. The fake Docker server now
        // reproduces this line deliberately, so the fold is covered without a daemon; the real-daemon test remains
        // the sentinel for the daemon REWORDING it, which no fake can be.
        if (message.Status.StartsWith("Pulling from", StringComparison.Ordinal))
        {
            return;
        }

        ref var layer = ref CollectionsMarshal.GetValueRefOrAddDefault(_layers, message.ID, out _);

        if (message.Status.StartsWith("Downloading", StringComparison.Ordinal)
            || message.Status.StartsWith("Extracting", StringComparison.Ordinal))
        {
            // Both counters are nullable on 4.3.3, and a layer whose total the daemon never states must contribute a
            // zero rather than be dropped: dropping it would shrink the denominator and make the pull look finished.
            layer.Current = message.Progress?.Current ?? 0;
            layer.Total = message.Progress?.Total ?? 0;
            return;
        }

        if (message.Status.StartsWith("Download complete", StringComparison.Ordinal)
            || message.Status.StartsWith("Pull complete", StringComparison.Ordinal)
            || message.Status.StartsWith("Already exists", StringComparison.Ordinal))
        {
            layer.Complete = true;
            if (layer.Total > 0)
            {
                layer.Current = layer.Total;
            }
        }
    }

    private struct LayerProgress
    {
        public long Current;
        public long Total;
        public bool Complete;
    }
}
