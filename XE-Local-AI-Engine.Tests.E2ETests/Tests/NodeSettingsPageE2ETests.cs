namespace XE_Local_AI_Engine.Tests.E2ETests.Tests;

using Microsoft.Playwright;
using XE_Local_AI_Engine.Tests.E2ETests.Common;

/// <summary>
///     Per-page interaction E2E tests for the node-settings page.
///     The real backend answers GET /api/local/v1/node-settings 200 in-process, so the
///     form populates from actual data stored in the per-session temp SQLite.
///     Save writes back to the same SQLite — harmless and verifiable via the success alert.
/// </summary>
[Category("Page")]
public sealed class NodeSettingsPageE2ETests : XEE2ETestBase
{
    [Test]
    public async Task NodeSettings_Page_Renders_Heading_And_Card()
    {
        await Page.GotoAsync($"{NodeAppUrl}/node-settings", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });

        // Main page heading rendered unconditionally.
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions
            {
                Name = "Node settings"
            }))
            .ToBeVisibleAsync();

        // Card heading for the runtime settings card.
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions
            {
                Name = "Local chat runtime"
            }))
            .ToBeVisibleAsync();
    }

    [Test]
    public async Task NodeSettings_Page_Timeout_Input_Populates_From_Backend()
    {
        await Page.GotoAsync($"{NodeAppUrl}/node-settings", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });

        // Use GetByLabel to locate the NumberInput via its Mantine <label for=...> association.
        // GetByText on the label element is unreliable because Mantine wraps label text in a span
        // inside a <label>, which Playwright's text locator does not surface as a top-level match.
        var timeoutInput = Page.GetByLabel("Maximum message request timeout");
        await Expect(timeoutInput).ToBeVisibleAsync();
        await Expect(timeoutInput).ToBeEnabledAsync();

        // The input must carry a numeric value — backend populated the form.
        var inputValue = await timeoutInput.InputValueAsync();
        var trimmed = inputValue.Replace(" seconds", string.Empty, StringComparison.Ordinal).Trim();
        await Assert.That(int.TryParse(trimmed, out var parsed) && parsed > 0).IsTrue();
    }

    [Test]
    public async Task NodeSettings_Page_Save_Settings_Shows_Success_Notification()
    {
        await Page.GotoAsync($"{NodeAppUrl}/node-settings", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });

        // Wait for the form to be populated from the backend before interacting.
        var timeoutInput = Page.GetByLabel("Maximum message request timeout");
        await Expect(timeoutInput).ToBeEnabledAsync();

        // Change the value to a known-valid number (300 seconds is within any reasonable range).
        await timeoutInput.FillAsync("300");

        // Save settings — button must be enabled after a valid change.
        var saveButton = Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
        {
            Name = "Save settings"
        });
        await Expect(saveButton).ToBeEnabledAsync();
        await saveButton.ClickAsync();

        // The component sets a green alert on successful save.
        await Expect(Page.GetByText("Node settings saved.").First).ToBeVisibleAsync();
    }
}
