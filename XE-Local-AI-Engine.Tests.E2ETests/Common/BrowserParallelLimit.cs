namespace XE_Local_AI_Engine.Tests.E2ETests.Common;

using TUnit.Core.Interfaces;
using XE_Local_AI_Engine.Tests.E2ETests.Infrastructure;

/// <summary>
///     Concurrency cap for the <c>BrowserSerial</c> group (<see cref="XESerialE2ETestBase" />), forced to
///     1: those tests mutate state the shared <c>PerTestSession</c> host exposes to every browser session,
///     or assert a node-wide empty state, and neither survives a concurrent sibling.
/// </summary>
/// <remarks>
///     That state: the <c>WorkerEventDispatcher.CurrentInvocation</c> slot, <c>FakeOllamaState</c>, the
///     canonical admin's tutorial row. It is not about auth: each sign-in is its own refresh session and the
///     remaining auth couplings (a logout revoking every session of its user, Identity lockout) are per-user,
///     so a user per test unlocks concurrency, as <see cref="PooledBrowserParallelLimit" /> and
///     <see cref="XEPooledE2ETestBase" /> do.
/// </remarks>
public sealed class BrowserParallelLimit : IParallelLimit
{
    public int Limit => 1;
}

/// <summary>
///     Concurrency cap for the <c>BrowserPooled</c> group (<see cref="XEPooledE2ETestBase" />). Equal to the
///     number of seeded pool users, because each concurrent test must hold a DISTINCT user: two browser
///     sessions sharing one user would sign each other out on logout.
/// </summary>
public sealed class PooledBrowserParallelLimit : IParallelLimit
{
    public int Limit => XENodeE2EWebApplicationFactory.PooledUserCount;
}
