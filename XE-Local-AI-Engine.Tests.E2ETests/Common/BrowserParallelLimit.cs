namespace XE_Local_AI_Engine.Tests.E2ETests.Common;

using TUnit.Core.Interfaces;
using XE_Local_AI_Engine.Tests.E2ETests.Infrastructure;

/// <summary>
///     Concurrency cap for the <c>BrowserSerial</c> group (<see cref="XESerialE2ETestBase" />). Forced to 1:
///     those tests mutate state the shared <c>PerTestSession</c> host exposes to every browser session at once
///     (the <c>WorkerEventDispatcher.CurrentInvocation</c> slot, <c>FakeOllamaState</c>, the canonical admin's
///     tutorial row) or assert a node-wide empty state, none of which survives a concurrent sibling.
///     <para>
///         This is NOT about the auth model. The node revokes all of a user's active refresh tokens on every
///         login/refresh (<c>NodeAuthService.RevokeActiveTokensAsync</c>), but that is strictly per-user — so
///         concurrency is unlocked by giving each test its own user, which is what
///         <see cref="PooledBrowserParallelLimit" /> and <see cref="XEPooledE2ETestBase" /> do.
///     </para>
/// </summary>
public sealed class BrowserParallelLimit : IParallelLimit
{
    public int Limit => 1;
}

/// <summary>
///     Concurrency cap for the <c>BrowserPooled</c> group (<see cref="XEPooledE2ETestBase" />). Equal to the
///     number of seeded pool users, because each concurrent test must hold a DISTINCT user: two browser
///     sessions sharing one user would revoke each other's refresh cookie mid-test.
/// </summary>
public sealed class PooledBrowserParallelLimit : IParallelLimit
{
    public int Limit => XENodeE2EWebApplicationFactory.PooledUserCount;
}
