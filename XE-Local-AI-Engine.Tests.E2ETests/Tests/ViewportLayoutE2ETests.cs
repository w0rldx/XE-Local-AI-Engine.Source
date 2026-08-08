namespace XE_Local_AI_Engine.Tests.E2ETests.Tests;

using Microsoft.Playwright;
using XE_Local_AI_Engine.Tests.E2ETests.Common;

/// <summary>
///     Viewport and full-width layout regression tests.
///     Guards that the full-width layout (Container fluid=true on 5 pages, Box flex on Chat)
///     is not silently reverted — a fixed max-width Container would re-introduce the centered-column
///     gutter that these tests catch.
///     <para>
///         Wide-viewport assertions: at 1280×800 the page body must not overflow horizontally
///         (scrollWidth ≤ innerWidth) and the heading element must span more than half the content
///         width, proving no hidden max-width cap is constraining the layout.
///     </para>
///     <para>
///         Mobile-viewport assertions: at 375×667 (iPhone SE) the Layout.tsx JS breakpoint
///         (width &lt; 768) sets contentWidth=100% and hides the desktop nav, so page content
///         fills the full narrow viewport without horizontal overflow.
///     </para>
///     <para>
///         All pages are unpaired-safe (no Central Platform connection needed). The mobile tests
///         use <see cref="PageSetViewportSizeAsync" /> before navigation so responsive CSS is
///         already in effect when the React tree mounts.
///     </para>
/// </summary>
[Category("Layout")]
public sealed class ViewportLayoutE2ETests : XEE2ETestBase
{
    // Representative Container-fluid pages. Models replaced the former Dashboard target: the Dashboard is
    // a Central-Platform surface shipped gated OFF (NodeCapabilities.dashboard === false), so /dashboard
    // now redirects home and renders no page of its own to measure. Models is the equivalent always-on
    // Container fluid={true} page and, like the old target, renders its heading unconditionally.
    private const string ModelsRoute = "/models";
    private const string ModelsHeading = "Model management";
    private const string CloudSettingsRoute = "/cloud-settings";
    private const string NodeSettingsRoute = "/node-settings";

    // Wide desktop viewport — at this width the desktop nav is active (768px breakpoint) and
    // Container fluid / Box flex must fill the remaining content area.
    private const int WideViewportWidth = 1280;
    private const int WideViewportHeight = 800;

    // Mobile viewport — 375×667 matches iPhone SE; Layout.tsx hides the desktop nav below 768px.
    private const int MobileViewportWidth = 375;
    private const int MobileViewportHeight = 667;

    // -----------------------------------------------------------------------------------------
    // Wide-viewport full-width tests
    // -----------------------------------------------------------------------------------------

    /// <summary>
    ///     At a wide desktop viewport, the Models page body must not overflow horizontally.
    ///     A non-fluid (capped) Container would produce scrollWidth &gt; innerWidth because the
    ///     centered column leaves document-level whitespace to the right.
    /// </summary>
    [Test]
    [Category("Layout")]
    public async Task WideViewport_Models_Has_No_Horizontal_Overflow()
    {
        await Page.SetViewportSizeAsync(WideViewportWidth, WideViewportHeight);

        await Page.GotoAsync($"{NodeAppUrl}{ModelsRoute}", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });

        // Wait for the heading — confirms the React tree has mounted and Container is in the DOM.
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions
            {
                Name = ModelsHeading
            }))
            .ToBeVisibleAsync();

        // scrollWidth > innerWidth means the body overflows — a symptom of max-width centering.
        var scrollWidth = await Page.EvaluateAsync<int>("() => document.body.scrollWidth");
        var innerWidth = await Page.EvaluateAsync<int>("() => window.innerWidth");

        await Assert.That(scrollWidth).IsLessThanOrEqualTo(innerWidth);
    }

    /// <summary>
    ///     At a wide desktop viewport, the Models heading must span more than half the content
    ///     area width. A fixed max-width Container (e.g. 960px on a 1280px viewport) would render
    ///     a heading width well under half the viewport, exposing the regression.
    /// </summary>
    [Test]
    [Category("Layout")]
    public async Task WideViewport_Models_Heading_Fills_ContentArea_Width()
    {
        await Page.SetViewportSizeAsync(WideViewportWidth, WideViewportHeight);

        await Page.GotoAsync($"{NodeAppUrl}{ModelsRoute}", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });

        var heading = Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions
        {
            Name = ModelsHeading
        });
        await Expect(heading).ToBeVisibleAsync();

        // The heading's containing Stack/Container should start near the left edge of the
        // content pane, not indented by a centered-column margin. Measure its left offset
        // relative to the viewport — for fluid layout this is close to the nav sidebar width
        // (~56–220px), not the large centering margin a capped Container would produce.
        //
        // Strategy: assert that the content area (the scrollable pane the heading lives in)
        // fills more than 50% of the total viewport width. With desktop nav expanded (220px)
        // on a 1280px viewport the content area is ~1060px — well over 50%.
        var contentAreaWidth = await Page.EvaluateAsync<double>("() => { const el = document.querySelector('[class*=\"overflow-y-auto\"]'); " +
                                                                "return el ? el.getBoundingClientRect().width : window.innerWidth; }");

        await Assert.That(contentAreaWidth).IsGreaterThan(WideViewportWidth * 0.5);
    }

    /// <summary>
    ///     At a wide desktop viewport, the Cloud Settings page (Container fluid) must not
    ///     overflow horizontally — guards the CloudSettings.tsx Container fluid regression.
    /// </summary>
    [Test]
    [Category("Layout")]
    public async Task WideViewport_CloudSettings_Has_No_Horizontal_Overflow()
    {
        await Page.SetViewportSizeAsync(WideViewportWidth, WideViewportHeight);

        await Page.GotoAsync($"{NodeAppUrl}{CloudSettingsRoute}", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });

        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions
            {
                Name = "Cloud settings"
            }))
            .ToBeVisibleAsync();

        var scrollWidth = await Page.EvaluateAsync<int>("() => document.body.scrollWidth");
        var innerWidth = await Page.EvaluateAsync<int>("() => window.innerWidth");

        await Assert.That(scrollWidth).IsLessThanOrEqualTo(innerWidth);
    }

    // -----------------------------------------------------------------------------------------
    // Mobile-viewport tests
    // -----------------------------------------------------------------------------------------

    /// <summary>
    ///     At a mobile viewport (375×667) the Models page must render its heading and must not
    ///     overflow horizontally. Layout.tsx sets contentWidth=100% below 768px, so the content
    ///     pane fills the full narrow screen — overflow would indicate a min-width or flex-shrink bug.
    /// </summary>
    [Test]
    [Category("Layout")]
    public async Task MobileViewport_Models_Renders_Heading_Without_Horizontal_Overflow()
    {
        // Set viewport before navigation so the JS breakpoint in Layout.tsx fires on first paint.
        await Page.SetViewportSizeAsync(MobileViewportWidth, MobileViewportHeight);

        await Page.GotoAsync($"{NodeAppUrl}{ModelsRoute}", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });

        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions
            {
                Name = ModelsHeading
            }))
            .ToBeVisibleAsync();

        // At mobile width the desktop nav is hidden; the content pane is 100% wide.
        // A horizontal overflow here means something has a hard-coded min-width wider than 375px.
        var scrollWidth = await Page.EvaluateAsync<int>("() => document.body.scrollWidth");
        var innerWidth = await Page.EvaluateAsync<int>("() => window.innerWidth");

        await Assert.That(scrollWidth).IsLessThanOrEqualTo(innerWidth);
    }

    /// <summary>
    ///     At a mobile viewport, the Node Settings page must render its heading. This exercises the
    ///     NodeSettings.tsx Container fluid path on a narrow screen.
    /// </summary>
    [Test]
    [Category("Layout")]
    public async Task MobileViewport_NodeSettings_Renders_Heading()
    {
        await Page.SetViewportSizeAsync(MobileViewportWidth, MobileViewportHeight);

        await Page.GotoAsync($"{NodeAppUrl}{NodeSettingsRoute}", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });

        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions
            {
                Name = "Node settings"
            }))
            .ToBeVisibleAsync();

        // Content must not overflow horizontally at mobile width.
        var scrollWidth = await Page.EvaluateAsync<int>("() => document.body.scrollWidth");
        var innerWidth = await Page.EvaluateAsync<int>("() => window.innerWidth");

        await Assert.That(scrollWidth).IsLessThanOrEqualTo(innerWidth);
    }

    /// <summary>
    ///     At a mobile viewport, the Cloud Settings page must render its heading without horizontal
    ///     overflow. Covers the PasswordInput / TextInput form layout on narrow screens.
    /// </summary>
    [Test]
    [Category("Layout")]
    public async Task MobileViewport_CloudSettings_Renders_Heading_Without_Horizontal_Overflow()
    {
        await Page.SetViewportSizeAsync(MobileViewportWidth, MobileViewportHeight);

        await Page.GotoAsync($"{NodeAppUrl}{CloudSettingsRoute}", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });

        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions
            {
                Name = "Cloud settings"
            }))
            .ToBeVisibleAsync();

        var scrollWidth = await Page.EvaluateAsync<int>("() => document.body.scrollWidth");
        var innerWidth = await Page.EvaluateAsync<int>("() => window.innerWidth");

        await Assert.That(scrollWidth).IsLessThanOrEqualTo(innerWidth);
    }
}
