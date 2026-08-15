namespace XE_Local_AI_Engine.Tests.E2ETests.Common;

using Microsoft.Playwright;
using XE_Local_AI_Engine.Tests.E2ETests.Infrastructure;

/// <summary>
///     Base for browser E2E tests that must run ONE AT A TIME as the canonical admin: they mutate
///     session-global state no other browser session may observe mid-flight (the shared
///     <c>WorkerEventDispatcher.CurrentInvocation</c> slot, <c>FakeOllamaState</c> scripts/models,
///     the admin's persisted tutorial row) or assert a node-wide empty state.
///     <para>
///         This is the <c>BrowserSerial</c> group. TUnit runs the two browser groups as DISJOINT phases —
///         no test from one group is ever in flight while a test from the other is (measured: 0 overlapping
///         pairs across a 69-test run) — so a global mutation made here can never race a pooled reader.
///         WHICH phase runs first is not guaranteed and must not be relied on: the distinct
///         <c>Order</c> values separate the phases, but the observed order was pooled-then-serial, i.e. the
///         opposite of ascending Order. Any test that needs a specific relative order needs a different
///         mechanism.
///     </para>
/// </summary>
// S101: matches the XEE2ETestBase harness naming; see that type for why the prefix is intentional.
#pragma warning disable S101 // Types should be named in PascalCase
[ParallelLimiter<BrowserParallelLimit>]
[ParallelGroup("BrowserSerial", Order = 0)]
public abstract class XESerialE2ETestBase : XEE2ETestBase
{
    protected override async Task SignInAsync()
    {
        // The harness seeds a single admin (XENodeE2EWebApplicationFactory.AdminEmail / AdminPassword),
        // so a fresh browser context lands on /login (not the one-time /setup screen). Drive the real
        // password login — that UI form is the shipped path and only these tests still exercise it.
        await Page.GotoAsync(NodeAppUrl, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        }).ConfigureAwait(false);

        // Target the input directly: the login form has a single password field, and Mantine's
        // PasswordInput also renders a "Toggle password visibility" button plus a required-asterisk
        // label, so GetByLabel("Password") is either ambiguous (matches the toggle) or empty (exact
        // misses the asterisk). The type='password' input is unique on this page.
        await Page.Locator("input[type='password']")
                  .FillAsync(XENodeE2EWebApplicationFactory.AdminPassword).ConfigureAwait(false);
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
        {
            Name = "Sign in"
        }).ClickAsync().ConfigureAwait(false);

        // On success the SPA navigates away from /login.
        await Page.WaitForURLAsync(url => !url.Contains("/login", StringComparison.OrdinalIgnoreCase)).ConfigureAwait(false);
    }
}
#pragma warning restore S101
