namespace XE_Local_AI_Engine.Tests.E2ETests.Common;

using System.Threading.Channels;
using Microsoft.Playwright;
using XE_Local_AI_Engine.Tests.E2ETests.Infrastructure;

/// <summary>
///     Base for browser E2E tests that are safe to run CONCURRENTLY: they only read node-global state or
///     mutate rows they name with a fresh <c>Guid</c>, so a sibling browser session cannot change what they
///     assert. Each test leases a distinct seeded user for the duration of the test, because the single
///     coupling that forbids concurrency is strictly per-user — <c>NodeAuthService.RevokeActiveTokensAsync</c>
///     revokes only the logging-in user's refresh tokens, and Identity lockout is per-user too.
///     <para>
///         This is the <c>BrowserPooled</c> group. TUnit runs the two browser groups as DISJOINT phases, so
///         none of these ever overlaps a serial test (measured: 0 overlapping pairs across a 69-test run).
///         WHICH phase runs first is not guaranteed — see <see cref="XESerialE2ETestBase" /> — so nothing
///         here may assume the serial group has or has not already run.
///     </para>
/// </summary>
// S101: matches the XEE2ETestBase harness naming; see that type for why the prefix is intentional.
#pragma warning disable S101 // Types should be named in PascalCase
[ParallelLimiter<PooledBrowserParallelLimit>]
[ParallelGroup("BrowserPooled", Order = 1)]
public abstract class XEPooledE2ETestBase : XEE2ETestBase
{
    // Pre-filled with every pool index; a test takes one for its duration and writes it back after.
    // Capacity == PooledUserCount == the parallel limit, so a reader never actually waits — the channel
    // is what maps "some slot is free" to "which user is free".
    private static readonly Channel<int> AvailableUserIndexes = CreateUserIndexChannel();

    private int? _leasedUserIndex;

    /// <summary>Email of the pooled user this test is signed in as. Set before the test body runs.</summary>
    protected string CurrentUserEmail { get; private set; } = string.Empty;

    protected override async Task SignInAsync()
    {
        var index = await AvailableUserIndexes.Reader.ReadAsync().ConfigureAwait(false);
        _leasedUserIndex = index;
        CurrentUserEmail = XENodeE2EWebApplicationFactory.PooledUserEmail(index);

        try
        {
            // The login FORM posts no email (single password field), so it can only ever resolve the one
            // SetupCompleted user. Pooled users are reached through the API with an explicit email, which
            // takes NodeAuthService's FindByEmailAsync branch. Context.APIRequest shares the BrowserContext
            // cookie jar, so the node_rt refresh cookie lands in the context and the SPA boots authenticated.
            var response = await Context.APIRequest.PostAsync($"{NodeAppUrl}/api/local/v1/auth/login", new APIRequestContextOptions
            {
                DataObject = new
                {
                    email = CurrentUserEmail,
                    password = XENodeE2EWebApplicationFactory.PooledUserPassword
                }
            }).ConfigureAwait(false);

            if (!response.Ok)
            {
                throw new InvalidOperationException($"Pooled E2E login for '{CurrentUserEmail}' failed with HTTP {response.Status} {response.StatusText}.");
            }

            await Page.GotoAsync(NodeAppUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.NetworkIdle
            }).ConfigureAwait(false);

            // Session-restore (auth/status -> auth/refresh) must have re-minted the access token from the
            // cookie; if it did not, the SPA parks on /login and this waits out rather than failing later
            // inside the test body with an unrelated assertion.
            await Page.WaitForURLAsync(url => !url.Contains("/login", StringComparison.OrdinalIgnoreCase)).ConfigureAwait(false);
        }
        catch
        {
            // A lease leaked here would permanently shrink the pool and eventually deadlock the group,
            // so release it on the failure path too rather than relying only on the [After(Test)] hook.
            await ReleasePooledUserAsync().ConfigureAwait(false);

            throw;
        }
    }

    [After(Test)]
    public async Task ReleasePooledUserAsync()
    {
        if (_leasedUserIndex is not { } index)
        {
            return;
        }

        _leasedUserIndex = null;
        await AvailableUserIndexes.Writer.WriteAsync(index).ConfigureAwait(false);
    }

    private static Channel<int> CreateUserIndexChannel()
    {
        var channel = Channel.CreateBounded<int>(XENodeE2EWebApplicationFactory.PooledUserCount);
        for (var index = 0; index < XENodeE2EWebApplicationFactory.PooledUserCount; index++)
        {
            if (!channel.Writer.TryWrite(index))
            {
                throw new InvalidOperationException("Failed to pre-fill the pooled E2E user channel.");
            }
        }

        return channel;
    }
}
#pragma warning restore S101
