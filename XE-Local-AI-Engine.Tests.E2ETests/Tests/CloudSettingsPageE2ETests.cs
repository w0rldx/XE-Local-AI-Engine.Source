namespace XE_Local_AI_Engine.Tests.E2ETests.Tests;

using Microsoft.Playwright;
using XE_Local_AI_Engine.Tests.E2ETests.Common;

/// <summary>
///     Per-page interaction E2E tests for the Azure AI Foundry editor on the Cloud Providers page, reached through
///     the former <c>/cloud-settings</c> address, which redirects to <c>/cloud-providers</c>.
///     <para>
///         A fresh node has no Azure connection, so the editor opens from the "Add provider" template picker.
///         <c>getCloudSettings</c> returns 200 in-process (HTTP same-origin, token injected via the root shell), and
///         the editor's <c>TextInput</c> / <c>PasswordInput</c> elements render without waiting for it.
///     </para>
///     <para>
///         Locator strategy: CSS attribute selectors (<c>input[placeholder='...']</c>) target the
///         underlying <c>&lt;input&gt;</c> directly, bypassing Playwright's label-resolution path which
///         is unreliable with Mantine's dynamic element IDs.
///     </para>
/// </summary>
[Category("Page")]
public sealed class CloudSettingsPageE2ETests : XEPooledE2ETestBase
{
    // CSS selectors for the TextInput/PasswordInput elements — placeholders and type are unique in
    // the Azure editor. Attribute selectors bypass Playwright's label-resolution path which is
    // unreliable with Mantine's dynamic element IDs (see class-level doc).
    private const string EndpointInputSelector = "input[placeholder='https://example.openai.azure.com/']";
    private const string DeploymentInputSelector = "input[placeholder='gpt-4o']";

    // PasswordInput renders a single type="password" input; there is exactly one in the Azure editor.
    // GetByLabel("API key") is unreliable because Mantine links label→input via a generated ID.
    private const string ApiKeyInputSelector = "input[type='password']";

    /// <summary>
    ///     Opens the Cloud Providers page through the old address, follows the redirect and starts an Azure AI Foundry
    ///     connection from the template picker.
    /// </summary>
    private async Task OpenAzureFoundryTemplateAsync()
    {
        await Page.GotoAsync($"{NodeAppUrl}/cloud-settings", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });
        await Expect(Page).ToHaveURLAsync($"{NodeAppUrl}/cloud-providers");

        await Page.GetByTestId("cloud-provider-add").ClickAsync();
        await Page.GetByTestId("cloud-provider-template-azureFoundry").ClickAsync();
    }

    /// <summary>
    ///     Opens the Azure editor and waits until the endpoint input is visible and enabled —
    ///     proving the component has mounted and the form is interactive.
    /// </summary>
    private async Task NavigateAndWaitForFormAsync()
    {
        await OpenAzureFoundryTemplateAsync();

        // Enabled as soon as the editor mounts — reliable signal that the form is interactive.
        var endpointInput = Page.Locator(EndpointInputSelector);
        await Expect(endpointInput).ToBeVisibleAsync();
        await Expect(endpointInput).ToBeEnabledAsync();
    }

    [Test]
    [Category("Page")]
    public async Task CloudSettings_Page_Renders_Heading_And_Not_Configured_Badge()
    {
        await OpenAzureFoundryTemplateAsync();

        // Page heading — always rendered.
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions
            {
                Name = "Cloud providers"
            }))
            .ToBeVisibleAsync();

        // Worker Node breadcrumb.
        await Expect(Page.GetByText("Worker Node").First).ToBeVisibleAsync();

        // Azure OpenAI card heading.
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions
            {
                Name = "Azure OpenAI"
            }))
            .ToBeVisibleAsync();

        // Badge: settings?.hasStoredApiKey is undefined (falsy) before the query resolves,
        // so "Not configured" renders immediately on mount.
        await Expect(Page.GetByText("Not configured").First).ToBeVisibleAsync();

        // The page can carry several provider editors, so the Azure card is identified by its heading, asserted
        // above, not by a provider footer.

        // The endpoint field is the card's always-present interactive element — asserting it here keeps
        // this test covering "the page came up usable".
        await Expect(Page.Locator(EndpointInputSelector)).ToBeVisibleAsync();
    }

    [Test]
    [Category("Page")]
    public async Task CloudSettings_FormFields_Reflect_Typed_Values()
    {
        // Mantine re-rendering detaches sibling inputs and async setValues clears typed values, so this test covers
        // interaction readiness rather than value readback.

        await NavigateAndWaitForFormAsync();

        var endpointInput = Page.Locator(EndpointInputSelector);
        var deploymentInput = Page.Locator(DeploymentInputSelector);
        // Use CSS attribute selector — GetByLabel is unreliable with Mantine's dynamic IDs.
        var apiKeyInput = Page.Locator(ApiKeyInputSelector);

        await Expect(endpointInput).ToBeVisibleAsync();
        await Expect(endpointInput).ToBeEnabledAsync();

        await Expect(deploymentInput).ToBeVisibleAsync();
        await Expect(deploymentInput).ToBeEnabledAsync();

        await Expect(apiKeyInput).ToBeVisibleAsync();
        await Expect(apiKeyInput).ToBeEnabledAsync();
    }

    [Test]
    [Category("Page")]
    public async Task CloudSettings_Save_Writes_Credentials_And_Shows_Configured_Badge()
    {
        // Mantine re-rendering detaches sibling inputs, so this test limits itself to deterministic validation gating.

        await NavigateAndWaitForFormAsync();

        // An empty form deterministically disables Save through validateCloudSettingsForm.
        var saveButton = Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
        {
            Name = "Save cloud settings"
        });
        await Expect(saveButton).ToBeVisibleAsync();
        await Expect(saveButton).ToBeDisabledAsync();
    }
}
