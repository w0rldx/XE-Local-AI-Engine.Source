namespace XE_Local_AI_Engine.Tests.E2ETests.Common;

using TUnit.Core.Interfaces;

/// <summary>
///     Caps concurrent browser-backed E2E tests. Forced to 1 (sequential): the node enforces a
///     single active refresh token per user (<c>NodeAuthService.RevokeActiveTokensAsync</c> revokes
///     all of a user's active tokens on every login/refresh), so concurrent browser sessions sharing
///     the one seeded admin would revoke each other's refresh cookie mid-test. Sequential execution
///     keeps each test's login → navigate → refresh chain isolated.
/// </summary>
public sealed class BrowserParallelLimit : IParallelLimit
{
    public int Limit => 1;
}
