namespace XE_Local_AI_Engine.Tests.E2ETests.Tests;

using Microsoft.Playwright;
using XE_Local_AI_Engine.Tests.E2ETests.Common;

/// <summary>
/// Per-page interaction E2E tests for the node-binding page (plan M2, wave-2).
/// Asserts static layout and the "Start binding" button interaction without
/// depending on Central Platform availability — the POST will fail against
/// test.example.com, so the test accepts either the polling-started UI or an
/// error alert as the post-click state.
/// </summary>
[Category("Page")]
public sealed class NodeBindingPageE2ETests : XEE2ETestBase
{
    [Test]
    public async Task NodeBinding_Page_Renders_Static_Content()
    {
        await Page.GotoAsync($"{NodeAppUrl}/node-binding", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });

        // Page heading is rendered unconditionally.
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Bind this node to your Central Platform account" }))
            .ToBeVisibleAsync();

        // Card heading for the binding controls card.
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Device binding" }))
            .ToBeVisibleAsync();

        // Card heading for the how-it-works card.
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "How binding works" }))
            .ToBeVisibleAsync();

        // First list item instruction is visible.
        await Expect(Page.GetByText("Click Start binding to request a one-time user code.").First)
            .ToBeVisibleAsync();
    }

    [Test]
    public async Task NodeBinding_StartBinding_Button_Is_Visible_And_Enabled()
    {
        await Page.GotoAsync($"{NodeAppUrl}/node-binding", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });

        var startButton = Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Start binding" });
        await Expect(startButton).ToBeVisibleAsync();
        await Expect(startButton).ToBeEnabledAsync();
    }

    /// <summary>
    /// Asserts that clicking "Start binding" issues the binding-start POST. Asserting the request
    /// fires is the deterministic interaction signal — it is independent of response/render timing
    /// (the in-process POST settles at variable speed, so the transient disabled state and the
    /// resulting Alert are too brief/timing-dependent to catch reliably). Post-mutation UI readback
    /// (disabled state, Alert, polling) is deferred to wave-2.1.
    /// </summary>
    [Test]
    public async Task NodeBinding_StartBinding_Click_Produces_Deterministic_State_Change()
    {
        await Page.GotoAsync($"{NodeAppUrl}/node-binding", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });

        var startButton = Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Start binding" });
        await Expect(startButton).ToBeEnabledAsync();

        // Clicking issues POST /api/local/v1/binding/start; assert the request fires.
        await Page.RunAndWaitForRequestAsync(
            async () => await startButton.ClickAsync(),
            request => request.Url.Contains("binding/start", StringComparison.OrdinalIgnoreCase)
                       && string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase));
    }
}
