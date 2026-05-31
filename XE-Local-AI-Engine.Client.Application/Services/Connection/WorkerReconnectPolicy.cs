namespace XE_Local_AI_Engine.Client.Services.Connection;

using Microsoft.AspNetCore.SignalR.Client;
using XE_Local_AI_Engine.Client.Configuration;

/// <summary>
///     Represents worker reconnect policy.
/// </summary>
public sealed class WorkerReconnectPolicy : IRetryPolicy
{
    private readonly int _baseDelayMs;
    private readonly int _jitterMs;
    private readonly int _maxAttempts;
    private readonly int _maxDelayMs;
    private readonly Action? _onCredentialsRevoked;
    private readonly Random _random;

    public WorkerReconnectPolicy(CentralPlatformOptions options)
        : this(options, Random.Shared)
    {
    }

    public WorkerReconnectPolicy(CentralPlatformOptions options, Action? onCredentialsRevoked)
        : this(options, Random.Shared, onCredentialsRevoked)
    {
    }

    internal WorkerReconnectPolicy(CentralPlatformOptions options, Random random, Action? onCredentialsRevoked = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _random = random ?? throw new ArgumentNullException(nameof(random));
        _baseDelayMs = options.ReconnectBackoffBaseMs;
        _maxDelayMs = options.ReconnectBackoffMaxMs;
        _jitterMs = options.ReconnectBackoffJitterMs;
        _maxAttempts = options.ReconnectMaxAttempts;
        _onCredentialsRevoked = onCredentialsRevoked;
    }

    public TimeSpan? NextRetryDelay(RetryContext retryContext)
    {
        ArgumentNullException.ThrowIfNull(retryContext);

        if (IsCredentialsRevoked(retryContext.RetryReason))
        {
            // Permanent credential revocation: stop reconnecting. SignalR discards the real RetryReason
            // when it raises Closed (it substitutes a synthetic "retries exhausted" exception), so we
            // notify the owner here. WorkerHubConnection latches this and maps the subsequent Closed event
            // to the Error (re-pairing required) state.
            _onCredentialsRevoked?.Invoke();
            return null;
        }

        return GetDelay((int)retryContext.PreviousRetryCount);
    }

    public TimeSpan? GetDelay(int previousRetryCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(previousRetryCount);

        if (_maxAttempts > 0 && previousRetryCount >= _maxAttempts)
        {
            return null;
        }

        var exponent = Math.Min(previousRetryCount, 30);
        var exponentialDelay = _baseDelayMs * (1L << exponent);
        var boundedDelay = (int)Math.Min(exponentialDelay, _maxDelayMs);
        var jitter = _jitterMs == 0 ? 0 : _random.Next(0, _jitterMs + 1);

        return TimeSpan.FromMilliseconds(Math.Min(boundedDelay + jitter, _maxDelayMs));
    }

    private static bool IsCredentialsRevoked(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is WorkerCredentialsRevokedException)
            {
                return true;
            }
        }

        return false;
    }
}
