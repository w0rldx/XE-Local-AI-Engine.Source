namespace XE_Local_AI_Engine.Client.Services.Development;

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Sandbox;

public interface IDevelopmentAttemptLiveBroker
{
    bool Register(Guid attemptId);
    bool TryPublish(DevelopmentAttemptLiveUpdate update);
    bool TryGetSnapshot(Guid attemptId, out DevelopmentAttemptLiveSnapshot snapshot);
    bool TryGetDeliveryReader(Guid attemptId, [NotNullWhen(true)] out ChannelReader<DevelopmentAttemptLiveUpdate>? reader);
    bool Complete(Guid attemptId);

    /// <summary>Makes one running command of a live attempt stoppable by its execution id; false when the attempt is not live.</summary>
    bool TrackCommand(Guid attemptId, string executionId, Func<CancellationToken, Task> stopCommand);

    void UntrackCommand(Guid attemptId, string executionId);

    /// <summary>Requests a stop of a command tracked for exactly this attempt; false when none is.</summary>
    Task<bool> TryStopCommandAsync(Guid attemptId, string executionId, CancellationToken cancellationToken = default);
}

public sealed class DevelopmentAttemptLiveBroker : IDevelopmentAttemptLiveBroker
{
    private readonly ConcurrentDictionary<Guid, AttemptState> _attempts = new();
    private readonly int _capacity;
    private readonly int _maxTextCharacters;
    private readonly TimeProvider _timeProvider;

    /// <summary>The last sequence handed out on this node: sequences rise across attempts, so a stream never reuses an earlier number.</summary>
    private long _sequence;

    public DevelopmentAttemptLiveBroker(IOptions<DevelopmentOptions> options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
        _capacity = options.Value.LiveChannelCapacity;
        _maxTextCharacters = options.Value.MaxLiveTextCharacters;
    }

    public bool Register(Guid attemptId)
    {
        // Seeded with the node-wide sequence: validation re-registers the coder attempt's id while that run's delivery loop may still
        // drain into the same hub group, and a fresh count from 0 would put the new stream's numbers under the stale ones.
        return attemptId != Guid.Empty
               && _attempts.TryAdd(attemptId, new AttemptState(_capacity)
               {
                   Sequence = Interlocked.Read(ref _sequence)
               });
    }

    public bool TryPublish(DevelopmentAttemptLiveUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (update.AttemptId == Guid.Empty || update.ProjectId == Guid.Empty || update.TaskId == Guid.Empty)
        {
            return false;
        }

        if (!_attempts.TryGetValue(update.AttemptId, out var state))
        {
            return false;
        }

        lock (state.Sync)
        {
            if (state.IsCompleted)
            {
                return false;
            }

            state.Sequence = Interlocked.Increment(ref _sequence);
            var sanitized = DevelopmentAttemptLiveSanitizer.Sanitize(update, _maxTextCharacters) with
            {
                Sequence = state.Sequence,
                OccurredAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
            };
            state.Latest = sanitized;

            // Command output may fill only half the channel, so a flood never leaves a lifecycle update evicting another one.
            if (sanitized.Kind == DevelopmentAttemptLiveUpdateKind.CommandOutput
                && state.Channel.Reader.Count >= _capacity - (_capacity / 2))
            {
                state.DroppedOrCoalesced++;
                return false;
            }

            if (state.Channel.Writer.TryWrite(sanitized))
            {
                return true;
            }

            state.DroppedOrCoalesced++;
            if (sanitized.IsReplaceable)
            {
                return false;
            }

            _ = state.Channel.Reader.TryRead(out _);
            return state.Channel.Writer.TryWrite(sanitized);
        }
    }

    public bool TryGetSnapshot(Guid attemptId, out DevelopmentAttemptLiveSnapshot snapshot)
    {
        if (!_attempts.TryGetValue(attemptId, out var state))
        {
            snapshot = new DevelopmentAttemptLiveSnapshot
            {
                AttemptId = attemptId,
                Watermark = 0,
                DroppedOrCoalescedUpdateCount = 0,
                Latest = null
            };
            return false;
        }

        lock (state.Sync)
        {
            snapshot = new DevelopmentAttemptLiveSnapshot
            {
                AttemptId = attemptId,
                Watermark = state.Sequence,
                DroppedOrCoalescedUpdateCount = state.DroppedOrCoalesced,
                Latest = state.Latest
            };
            return !state.IsCompleted;
        }
    }

    public bool TryGetDeliveryReader(Guid attemptId, [NotNullWhen(true)] out ChannelReader<DevelopmentAttemptLiveUpdate>? reader)
    {
        if (!_attempts.TryGetValue(attemptId, out var state))
        {
            reader = null;
            return false;
        }

        lock (state.Sync)
        {
            reader = state.IsCompleted ? null : state.Channel.Reader;
            return reader is not null;
        }
    }

    public bool Complete(Guid attemptId)
    {
        if (!_attempts.TryRemove(attemptId, out var state))
        {
            return false;
        }

        lock (state.Sync)
        {
            state.IsCompleted = true;
            state.Channel.Writer.TryComplete();
            state.Commands.Clear();
        }

        return true;
    }

    public bool TrackCommand(Guid attemptId, string executionId, Func<CancellationToken, Task> stopCommand)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
        ArgumentNullException.ThrowIfNull(stopCommand);
        if (!_attempts.TryGetValue(attemptId, out var state))
        {
            return false;
        }

        lock (state.Sync)
        {
            return !state.IsCompleted && state.Commands.TryAdd(executionId, stopCommand);
        }
    }

    public void UntrackCommand(Guid attemptId, string executionId)
    {
        if (_attempts.TryGetValue(attemptId, out var state))
        {
            lock (state.Sync)
            {
                _ = state.Commands.Remove(executionId);
            }
        }
    }

    public async Task<bool> TryStopCommandAsync(Guid attemptId, string executionId, CancellationToken cancellationToken = default)
    {
        Func<CancellationToken, Task>? stop = null;
        if (_attempts.TryGetValue(attemptId, out var state))
        {
            lock (state.Sync)
            {
                _ = state.Commands.TryGetValue(executionId, out stop);
            }
        }

        if (stop is null)
        {
            return false;
        }

        try
        {
            await stop(cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is SandboxHandleInvalidException or ObjectDisposedException)
        {
            // The command finished and its sandbox, or its cancel source, went away between the lookup and the cancel.
            return false;
        }
    }

    private sealed class AttemptState
    {
        public AttemptState(int capacity)
        {
            Channel = System.Threading.Channels.Channel.CreateBounded<DevelopmentAttemptLiveUpdate>(new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                // Non-replaceable updates may evict one queued replaceable update from TryPublish while the
                // delivery loop is reading concurrently, so the channel must retain multi-reader synchronization.
                SingleReader = false,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
        }

        public object Sync { get; } = new();
        public Channel<DevelopmentAttemptLiveUpdate> Channel { get; }
        public long Sequence { get; set; }
        public long DroppedOrCoalesced { get; set; }
        public DevelopmentAttemptLiveUpdate? Latest { get; set; }

        /// <summary>The attempt's running, stoppable commands by sandbox execution id.</summary>
        public Dictionary<string, Func<CancellationToken, Task>> Commands { get; } = new(StringComparer.Ordinal);

        public bool IsCompleted { get; set; }
    }
}
