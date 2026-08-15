namespace XE_Local_AI_Engine.Tests.E2ETests.Tests;

using Microsoft.Playwright;
using XE_Local_AI_Engine.Tests.E2ETests.Common;

/// <summary>
///     First browser-driven smoke tests for the XE node React client, ordered by risk.
///     Both navigate to the root-hosted SPA shell.
/// </summary>
public sealed class NodeUiSmokeE2ETests : XEPooledE2ETestBase
{
    [Test]
    [Category("Smoke")]
    public async Task Models_Page_Renders_For_Unpaired_Node()
    {
        // Replaces the former /dashboard smoke test: the Dashboard is a Central-Platform surface and the
        // default build ships it gated OFF (NodeCapabilities.dashboard === false), so /dashboard redirects
        // home and has no heading to smoke. Models is the equivalent always-on page — no route guard, and
        // it renders unconditionally for an unpaired node. The gate itself is covered by
        // CapabilityGatedSurfacesE2ETests.
        await Page.GotoAsync($"{NodeAppUrl}/models", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });

        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions
            {
                Name = "Model management"
            }))
            .ToBeVisibleAsync();
        await Expect(Page.GetByText("Worker Node").First).ToBeVisibleAsync();
    }

    [Test]
    [Category("Smoke")]
    public async Task Models_Page_Renders_FakeOllama_Model()
    {
        await Page.GotoAsync($"{NodeAppUrl}/models", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });

        // A later iteration will replace this with the real setup/login browser flow.
        await Expect(Page.GetByText("qwen3.5:0.8b").First).ToBeVisibleAsync();
    }

    [Test]
    [Category("Smoke")]
    public async Task Root_Route_Renders_Home_Welcome()
    {
        await Page.GotoAsync($"{NodeAppUrl}/", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });

        // Root "/" is the _layout index route rendering the Home page.
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions
            {
                Name = "Welcome!"
            }))
            .ToBeVisibleAsync();
    }
}
