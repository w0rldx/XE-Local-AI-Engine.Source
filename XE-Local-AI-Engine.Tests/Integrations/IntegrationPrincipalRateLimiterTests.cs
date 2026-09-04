namespace XE_Local_AI_Engine.Tests.Integrations;

using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Services.Integrations;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The per-principal ceiling, driven with no host and no middleware — which is the point of moving fairness out of
///     the rate-limiting middleware. The load-bearing test is that one principal exhausting its window does NOT refuse
///     another: the shared per-IP layer cannot provide that on a loopback-only surface, and that gap is why ruling R5-5
///     added this layer at all.
/// </summary>
public sealed class IntegrationPrincipalRateLimiterTests
{
    [Test]
    public void TryAcquire_AllowsExactlyThePermitLimitWithinOneWindow()
    {
        using var limiter = new IntegrationPrincipalRateLimiter(permitLimit: 3);
        const string principal = "principal-a";

        AssertEx.True(limiter.TryAcquire(principal));
        AssertEx.True(limiter.TryAcquire(principal));
        AssertEx.True(limiter.TryAcquire(principal));
        AssertEx.False(limiter.TryAcquire(principal), "The fourth request in the window must be refused.");
    }

    [Test]
    public void TryAcquire_OneExhaustedPrincipalDoesNotRefuseAnother()
    {
        using var limiter = new IntegrationPrincipalRateLimiter(permitLimit: 2);

        AssertEx.True(limiter.TryAcquire("principal-a"));
        AssertEx.True(limiter.TryAcquire("principal-a"));
        AssertEx.False(limiter.TryAcquire("principal-a"));

        AssertEx.True(limiter.TryAcquire("principal-b"), "Fairness is the whole reason this layer exists: one integrator must not starve another.");
        AssertEx.True(limiter.TryAcquire("principal-b"));
        AssertEx.False(limiter.TryAcquire("principal-b"));
    }

    [Test]
    public void TryAcquire_NeverBlocks_BecauseTheQueueLimitIsZero()
    {
        using var limiter = new IntegrationPrincipalRateLimiter(permitLimit: 1);
        _ = limiter.TryAcquire("principal-a");

        var stopwatch = Stopwatch.StartNew();
        var refused = limiter.TryAcquire("principal-a");
        stopwatch.Stop();

        AssertEx.False(refused);
        AssertEx.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1),
            "A refusal must be immediate: queueing would turn a fast 429 into a held connection on an inference-bound surface.");
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    public void Constructor_RejectsANonPositivePermitLimit(int permitLimit) =>
        _ = AssertEx.Throws<ArgumentOutOfRangeException>(() =>
        {
            using var limiter = new IntegrationPrincipalRateLimiter(permitLimit);
        });

    [Test]
    public void TryAcquire_WithABlankPrincipal_Throws()
    {
        using var limiter = new IntegrationPrincipalRateLimiter(permitLimit: 5);

        _ = AssertEx.Throws<ArgumentException>(() => limiter.TryAcquire(string.Empty));
    }

    [Test]
    public void Limiter_IsDisposedWithTheContainer()
    {
        // An undisposed PartitionedRateLimiter roots its replenishment timer and, through it, the whole host graph.
        // Registering it through a factory is what makes the container own it; this asserts that ownership.
        var services = new ServiceCollection();
        services.AddSingleton(_ => new IntegrationPrincipalRateLimiter(permitLimit: 5));
        var provider = services.BuildServiceProvider();
        var limiter = provider.GetRequiredService<IntegrationPrincipalRateLimiter>();
        AssertEx.True(limiter.TryAcquire("principal-a"));

        provider.Dispose();

        _ = AssertEx.Throws<ObjectDisposedException>(() => limiter.TryAcquire("principal-a"));
    }
}
