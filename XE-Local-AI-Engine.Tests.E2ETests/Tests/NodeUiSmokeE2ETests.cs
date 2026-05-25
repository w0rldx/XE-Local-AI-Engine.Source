namespace XE_Local_AI_Engine.Tests.E2ETests.Tests;

using Microsoft.Playwright;
using XE_Local_AI_Engine.Tests.E2ETests.Common;

/// <summary>
///     First browser-driven smoke tests for the XE node React client, ordered by risk (plan M2):
///     #1 is unpaired-safe (no authenticated data needed); #2 exercises the operator-token bootstrap
///     end-to-end in a real browser by rendering FakeOllama models from <c>/api/local/v1/models</c>.
///     Both navigate to the token-injecting root route so the SPA receives the operator token.
/// </summary>
public sealed class NodeUiSmokeE2ETests : XEE2ETestBase
{
    [Test]
    [Category("Smoke")]
    public async Task Dashboard_Renders_For_Unpaired_Node()
    {
        // /dashboard is served token-injected (deep link hits ServeNodeReactIndexAsync).
        await Page.GotoAsync($"{NodeAppUrl}/dashboard", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });

        // The header renders unconditionally (no route guard redirects an unpaired node).
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions
            {
                Name = "Dashboard"
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

        // Proves the in-browser token bootstrap: a 200 from /api/local/v1/models surfaces a
        // FakeOllama model name configured by the factory ("qwen3.5:0.8b").
        await Expect(Page.GetByText("qwen3.5:0.8b").First).ToBeVisibleAsync();
    }
}
