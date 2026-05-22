namespace XE_Local_AI_Engine.Tests.Connection;

using Microsoft.AspNetCore.SignalR.Client;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Services.Connection;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class WorkerReconnectPolicyTests
{
    [Test]
    public void NextRetryDelay_WhenRetryReasonIsCredentialsRevoked_ReturnsNull()
    {
        var policy = CreatePolicy(0);

        var delay = policy.NextRetryDelay(new RetryContext
        {
            PreviousRetryCount = 0,
            ElapsedTime = TimeSpan.Zero,
            RetryReason = new WorkerCredentialsRevokedException()
        });

        AssertEx.Null(delay);
    }

    [Test]
    public void NextRetryDelay_WhenRetryReasonWrapsCredentialsRevoked_ReturnsNull()
    {
        var policy = CreatePolicy(0);

        var wrapped = new InvalidOperationException("transport failed",
            new HttpRequestException("negotiate failed", new WorkerCredentialsRevokedException()));

        var delay = policy.NextRetryDelay(new RetryContext
        {
            PreviousRetryCount = 0,
            ElapsedTime = TimeSpan.Zero,
            RetryReason = wrapped
        });

        AssertEx.Null(delay);
    }

    [Test]
    public void NextRetryDelay_WhenRetryReasonIsTransient_ReturnsNonNullDelay()
    {
        var policy = CreatePolicy(0);

        var delay = policy.NextRetryDelay(new RetryContext
        {
            PreviousRetryCount = 0,
            ElapsedTime = TimeSpan.Zero,
            RetryReason = new HttpRequestException("transient network error")
        });

        AssertEx.True(delay.HasValue, "Expected a non-null retry delay for a transient failure.");
        AssertEx.Equal(TimeSpan.FromMilliseconds(1000), delay!.Value);
    }

    [Test]
    public void NextRetryDelay_WhenAttemptsExhaustedWithTransientReason_ReturnsNull()
    {
        var policy = CreatePolicy(2);

        var delay = policy.NextRetryDelay(new RetryContext
        {
            PreviousRetryCount = 2,
            ElapsedTime = TimeSpan.Zero,
            RetryReason = new HttpRequestException("transient network error")
        });

        AssertEx.Null(delay);
    }

    private static WorkerReconnectPolicy CreatePolicy(int reconnectMaxAttempts)
    {
        return new WorkerReconnectPolicy(new CentralPlatformOptions
        {
            BaseUrl = "https://test.example.com",
            ReconnectBackoffBaseMs = 1000,
            ReconnectBackoffMaxMs = 30000,
            ReconnectBackoffJitterMs = 0,
            ReconnectMaxAttempts = reconnectMaxAttempts
        });
    }

    [Test]
    public void GetDelay_WhenAttemptsRemain_ReturnsExponentialBackoffWithinBounds()
    {
        var policy = new WorkerReconnectPolicy(new CentralPlatformOptions
        {
            BaseUrl = "https://test.example.com",
            ReconnectBackoffBaseMs = 1000,
            ReconnectBackoffMaxMs = 30000,
            ReconnectBackoffJitterMs = 0,
            ReconnectMaxAttempts = 3
        });

        AssertEx.Equal(TimeSpan.FromMilliseconds(1000), policy.GetDelay(0));
        AssertEx.Equal(TimeSpan.FromMilliseconds(2000), policy.GetDelay(1));
        AssertEx.Equal(TimeSpan.FromMilliseconds(4000), policy.GetDelay(2));
    }

    [Test]
    public void GetDelay_WhenAttemptsExhausted_ReturnsNull()
    {
        var policy = new WorkerReconnectPolicy(new CentralPlatformOptions
        {
            BaseUrl = "https://test.example.com",
            ReconnectBackoffBaseMs = 1000,
            ReconnectBackoffMaxMs = 30000,
            ReconnectBackoffJitterMs = 0,
            ReconnectMaxAttempts = 2
        });

        AssertEx.Null(policy.GetDelay(2));
    }

    [Test]
    public void GetDelay_WithDefaultOptions_CapsAtThirtyMinutes()
    {
        var policy = new WorkerReconnectPolicy(new CentralPlatformOptions
        {
            BaseUrl = "https://test.example.com"
        });

        AssertEx.Equal(TimeSpan.FromMinutes(30), policy.GetDelay(11));
        AssertEx.Equal(TimeSpan.FromMinutes(30), policy.GetDelay(12));
    }
}
