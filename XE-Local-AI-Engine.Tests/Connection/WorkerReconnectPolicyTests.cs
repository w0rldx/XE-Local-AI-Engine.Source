namespace XE_Local_AI_Engine.Tests.Connection;

using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Services.Connection;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class WorkerReconnectPolicyTests
{
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
}
