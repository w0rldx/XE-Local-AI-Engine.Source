namespace XE_Local_AI_Engine.Tests.E2ETests.Tests;

using Microsoft.Playwright;
using XE_Local_AI_Engine.Tests.E2ETests.Common;

/// <summary>
///     Retired Central Platform surfaces are absent from navigation and their former URLs render not found.
/// </summary>
[Category("Page")]
public sealed class CapabilityGatedSurfacesE2ETests : XEPooledE2ETestBase
{
    [Test]
    [Category("Page")]
    public async Task Retired_Dashboard_Route_Renders_Not_Found()
    {
        await Page.GotoAsync($"{NodeAppUrl}/dashboard", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });

        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions
            {
                Name = "404",
                Exact = true
            }))
            .ToBeVisibleAsync();

        await Expect(Page).ToHaveURLAsync($"{NodeAppUrl}/dashboard");
    }

    [Test]
    [Category("Page")]
    public async Task Retired_NodeBinding_Route_Renders_Not_Found()
    {
        await Page.GotoAsync($"{NodeAppUrl}/node-binding", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });

        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions
            {
                Name = "404",
                Exact = true
            }))
            .ToBeVisibleAsync();

        await Expect(Page).ToHaveURLAsync($"{NodeAppUrl}/node-binding");
    }

    [Test]
    [Category("Page")]
    public async Task Retired_Surfaces_Are_Absent_From_The_Navigation()
    {
        await Page.GotoAsync($"{NodeAppUrl}/", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });

        var navigation = Page.GetByRole(AriaRole.Navigation);
        // Anchor on a link that IS shipped first, so an empty / not-yet-rendered nav cannot make the two
        // absence assertions below pass vacuously.
        await Expect(navigation.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions
            {
                Name = "Models"
            }))
            .ToBeVisibleAsync();

        await Expect(navigation.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions
            {
                Name = "Dashboard"
            }))
            .ToHaveCountAsync(0);
        await Expect(navigation.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions
            {
                Name = "Node binding"
            }))
            .ToHaveCountAsync(0);
    }
}
