namespace XE_Local_AI_Engine.Tests.E2ETests.Tests;

using System.Collections.Concurrent;
using Microsoft.Playwright;
using XE_Local_AI_Engine.Tests.E2ETests.Common;

/// <summary>
///     One smoke test each for the three shipped read-only routes that had no browser coverage at all:
///     <c>/diagnostics</c> (local snapshot store), <c>/preview</c> (Open Canvas) and <c>/usage</c> (token-usage
///     dashboard). Each asserts the same three things:
///     <list type="bullet">
///         <item>The page's own header renders (so a capability redirect to home cannot read as a pass).</item>
///         <item>Its main region settles into a real state rather than staying on a loader or an error panel.</item>
///         <item>Loading it raises no page-level JS error and no 5xx from any API call it makes.</item>
///     </list>
///     <para>
///         The server-error check is deliberately scoped to 5xx rather than "no console error of severity error":
///         console output on these pages also carries third-party and framework noise (aborted polls on unmount,
///         SignalR reconnect chatter) that is not a product defect, so asserting on it would make the suite flaky
///         without making it stricter. A page-level JS error and a 500 from the node are unambiguous.
///     </para>
///     <para>
///         POOLED: all three pages are read-only. They read node-global state, but only in the weak sense that they
///         render whatever exists — no assertion here depends on a node-wide count or emptiness, so a concurrent
///         sibling writing unrelated rows cannot break them.
///     </para>
/// </summary>
[Category("Page")]
public sealed class ReadOnlyPagesE2ETests : XEPooledE2ETestBase
{
    private readonly ConcurrentBag<string> _pageErrors = [];
    private readonly ConcurrentBag<string> _serverErrors = [];

    private async Task NavigateAndWatchAsync(string path, string heading)
    {
        Page.PageError += (_, error) => _pageErrors.Add(error);
        Page.Response += (_, response) =>
        {
            if (response.Status >= 500)
            {
                _serverErrors.Add($"{response.Status} {response.Request.Method} {response.Url}");
            }
        };

        await Page.GotoAsync($"{NodeAppUrl}{path}", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });

        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions
            {
                Name = heading
            }))
            .ToBeVisibleAsync();
    }

    private async Task AssertNoErrorsAsync()
    {
        await Assert.That(string.Join(" | ", _serverErrors)).IsEqualTo(string.Empty);
        await Assert.That(string.Join(" | ", _pageErrors)).IsEqualTo(string.Empty);
    }

    [Test]
    [Category("Page")]
    public async Task Diagnostics_Page_Renders_Its_Local_Snapshot_Store()
    {
        await NavigateAndWatchAsync("/diagnostics", "Diagnostics");

        await Expect(Page.GetByTestId("diagnostics-page")).ToBeVisibleAsync();

        // Snapshots live in this browser context's IndexedDB, which is fresh per test, so the empty state is the
        // deterministic outcome here — no other suite can have written one into this context.
        await Expect(Page.GetByTestId("diagnostics-empty")).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
        {
            Timeout = 10_000
        });

        await AssertNoErrorsAsync();
    }

    [Test]
    [Category("Page")]
    public async Task Preview_Page_Renders_The_Workflow_List()
    {
        await NavigateAndWatchAsync("/preview", "Open Canvas");

        await Expect(Page.GetByTestId("preview-create-button")).ToBeEnabledAsync();

        // The list settles into one of its two success shapes; which one depends on whether a sibling has saved a
        // workflow, so neither alone may be asserted.
        await Expect(Page.Locator("[data-testid='preview-workflows-empty'], [data-testid='preview-workflows-table']").First)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
            {
                Timeout = 10_000
            });

        await AssertNoErrorsAsync();
    }

    [Test]
    [Category("Page")]
    public async Task Usage_Page_Renders_The_Token_Usage_Dashboard()
    {
        await NavigateAndWatchAsync("/usage", "Usage dashboard");

        // The range picker is unconditional page furniture.
        await Expect(Page.GetByTestId("usage-date-range")).ToBeVisibleAsync();

        // Totals when any agent run was recorded in range, the empty panel when none was — both are success; the
        // loading skeleton and the error alert are not.
        await Expect(Page.Locator("[data-testid='usage-totals'], [data-testid='usage-empty']").First)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
            {
                Timeout = 10_000
            });
        await Expect(Page.GetByTestId("usage-error")).ToHaveCountAsync(0);

        await AssertNoErrorsAsync();
    }
}
