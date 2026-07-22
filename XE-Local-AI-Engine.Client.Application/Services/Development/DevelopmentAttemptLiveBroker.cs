namespace XE_Local_AI_Engine.Client.Services.Development;

using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Options;

public interface IDevelopmentAttemptLiveBroker
{
    bool Register(Guid attemptId);
    bool TryPublish(DevelopmentAttemptLiveUpdate update);
    bool TryGetSnapshot(Guid attemptId, out DevelopmentAttemptLiveSnapshot snapshot);
    bool TryGetDeliveryReader(Guid attemptId, out ChannelReader<DevelopmentAttemptLiveUpdate>? reader);
    bool Complete(Guid attemptId);
}

public sealed class DevelopmentAttemptLiveBroker : IDevelopmentAttemptLiveBroker
{
    private readonly ConcurrentDictionary<Guid, AttemptState> _attempts = new();
    private readonly int _capacity;
    private readonly int _maxTextCharacters;
    private readonly TimeProvider _timeProvider;

    public DevelopmentAttemptLiveBroker(IOptions<DevelopmentOptions> options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _capacity = options.Value.LiveChannelCapacity;
        _maxTextCharacters = options.Value.MaxLiveTextCharacters;
    }

    public bool Register(Guid attemptId)
    {
        return attemptId != Guid.Empty && _attempts.TryAdd(attemptId, new AttemptState(_capacity));
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

            var sanitized = DevelopmentAttemptLiveSanitizer.Sanitize(update, _maxTextCharacters) with
            {
                Sequence = ++state.Sequence,
                OccurredAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
            };
            state.Latest = sanitized;
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
            snapshot = new DevelopmentAttemptLiveSnapshot(attemptId, 0, 0, null);
            return false;
        }

        lock (state.Sync)
        {
            snapshot = new DevelopmentAttemptLiveSnapshot(attemptId,
                state.Sequence,
                state.DroppedOrCoalesced,
                state.Latest);
            return !state.IsCompleted;
        }
    }

    public bool TryGetDeliveryReader(Guid attemptId, out ChannelReader<DevelopmentAttemptLiveUpdate>? reader)
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
        }

        return true;
    }

    private sealed class AttemptState
    {
        public AttemptState(int capacity)
        {
            Channel = System.Threading.Channels.Channel.CreateBounded<DevelopmentAttemptLiveUpdate>(new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
        }

        public object Sync { get; } = new();
        public Channel<DevelopmentAttemptLiveUpdate> Channel { get; }
        public long Sequence { get; set; }
        public long DroppedOrCoalesced { get; set; }
        public DevelopmentAttemptLiveUpdate? Latest { get; set; }
        public bool IsCompleted { get; set; }
    }
}
