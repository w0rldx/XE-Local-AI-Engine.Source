namespace XE_Local_AI_Engine.Tests.E2ETests.Tests;

using Microsoft.Playwright;
using XE_Local_AI_Engine.Tests.E2ETests.Common;

/// <summary>
///     Per-page interaction E2E tests for the invocation-monitor page.
///     The real backend returns an empty monitor in the test host, so assertions target
///     static layout and the empty-state text rather than live invocation data.
/// </summary>
[Category("Page")]
public sealed class InvocationsPageE2ETests : XESerialE2ETestBase
{
    [Test]
    public async Task Invocations_Page_Renders_Heading_And_Cards()
    {
        await Page.GotoAsync($"{NodeAppUrl}/invocations", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });

        // Main page heading.
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions
            {
                Name = "Invocation monitor"
            }))
            .ToBeVisibleAsync();

        // History card heading.
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions
            {
                Name = "Invocation history"
            }))
            .ToBeVisibleAsync();
    }

    [Test]
    public async Task Invocations_Page_Renders_Table_Column_Headers()
    {
        await Page.GotoAsync($"{NodeAppUrl}/invocations", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });

        // Table column headers defined in the component.
        await Expect(Page.GetByRole(AriaRole.Columnheader, new PageGetByRoleOptions
            {
                Name = "Invocation"
            }))
            .ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Columnheader, new PageGetByRoleOptions
            {
                Name = "Status"
            }))
            .ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Columnheader, new PageGetByRoleOptions
            {
                Name = "Model"
            }))
            .ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Columnheader, new PageGetByRoleOptions
            {
                Name = "Completed"
            }))
            .ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Columnheader, new PageGetByRoleOptions
            {
                Name = "Duration"
            }))
            .ToBeVisibleAsync();
    }

    [Test]
    public async Task Invocations_Page_Shows_Empty_State_For_No_Active_Invocation()
    {
        await Page.GotoAsync($"{NodeAppUrl}/invocations", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });

        // The backend returns null current invocation in the test host.
        await Expect(Page.GetByText("No invocation is currently assigned or running.").First)
            .ToBeVisibleAsync();
    }

    [Test]
    public async Task Invocations_Page_Refresh_Button_Keeps_Page_Stable()
    {
        await Page.GotoAsync($"{NodeAppUrl}/invocations", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });

        // Refresh button is visible and clickable.
        var refreshButton = Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
        {
            Name = "Refresh"
        });
        await Expect(refreshButton).ToBeVisibleAsync();
        await refreshButton.ClickAsync();

        // After refetch the heading must still be present — page did not navigate away or error.
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions
            {
                Name = "Invocation monitor"
            }))
            .ToBeVisibleAsync();
    }
}
