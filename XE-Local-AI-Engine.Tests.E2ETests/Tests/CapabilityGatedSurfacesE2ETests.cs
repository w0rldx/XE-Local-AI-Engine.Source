namespace XE_Local_AI_Engine.Tests.E2ETests.Tests;

using System.Text.RegularExpressions;
using Microsoft.Playwright;
using XE_Local_AI_Engine.Tests.E2ETests.Common;

/// <summary>
///     End-to-end contract for the Central-Platform surfaces that the default (local-only) build ships
///     DISABLED: Dashboard and Node Binding.
///     <para>
///         <c>NodeCapabilities.ts</c> sets <c>dashboard: false</c> and <c>binding: false</c> (commit
///         7d0c76d6, "fix(ui): hide Dashboard + Node Binding in local-only builds"), so both route files
///         throw a <c>redirect</c> to home in <c>beforeLoad</c> and <c>NavigationMenuData</c> filters
///         their nav entries out. Both flags are compile-time constants — there is no runtime switch a
///         test could flip — so the previous per-control Dashboard / Node-Binding E2E suites asserted a
///         surface this build cannot render and could never pass. They are replaced by this contract:
///         the gate itself is what ships, so the gate is what E2E guards.
///     </para>
///     <para>
///         When a build flips either capability on, restore the per-control coverage from git history
///         (<c>DashboardRemoteConnectionE2ETests.cs</c> / <c>NodeBindingPageE2ETests.cs</c>, removed
///         alongside this file's addition) rather than writing it from scratch — the page components
///         (<c>features/dashboard</c>, <c>features/node-binding</c>) still ship unchanged.
///     </para>
/// </summary>
[Category("Page")]
public sealed class CapabilityGatedSurfacesE2ETests : XEPooledE2ETestBase
{
    /// <summary>The heading of the home route every gated surface redirects to.</summary>
    private const string HomeHeading = "Welcome!";

    [Test]
    [Category("Page")]
    public async Task Dashboard_Route_Redirects_Home_While_Capability_Is_Off()
    {
        await Page.GotoAsync($"{NodeAppUrl}/dashboard", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });

        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions
            {
                Name = HomeHeading
            }))
            .ToBeVisibleAsync();

        // The redirect must rewrite the URL, not merely render home under the gated path — a deep link
        // that silently keeps /dashboard in the address bar would be reload-unstable.
        await Expect(Page).Not.ToHaveURLAsync(new Regex("/dashboard"));
    }

    [Test]
    [Category("Page")]
    public async Task NodeBinding_Route_Redirects_Home_While_Capability_Is_Off()
    {
        await Page.GotoAsync($"{NodeAppUrl}/node-binding", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });

        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions
            {
                Name = HomeHeading
            }))
            .ToBeVisibleAsync();

        await Expect(Page).Not.ToHaveURLAsync(new Regex("/node-binding"));
    }

    [Test]
    [Category("Page")]
    public async Task Gated_Surfaces_Are_Absent_From_The_Navigation()
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
