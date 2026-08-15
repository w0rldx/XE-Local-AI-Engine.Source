namespace XE_Local_AI_Engine.Tests.E2ETests.Tests;

using Microsoft.Playwright;
using XE_Local_AI_Engine.Tests.E2ETests.Common;

/// <summary>
///     Browser-driven E2E for the local image-generation page (<c>/images</c>) — a shipped route with no browser
///     coverage at all.
///     <para>
///         Scope is deliberately the NO-RUNTIME state, because that is the only honest headless state: there is no
///         fake <c>sd-server</c> in the test tree (FakeOllama backs chat/embeddings only), and the image model
///         registry reads an on-disk manifest under the fixture's temp <c>NodeData</c> directory, which is empty. So a
///         real generation cannot be driven here and is not attempted — the backend transaction suites own it. What
///         E2E owns is that the page degrades correctly with nothing installed instead of erroring or offering a
///         submit that cannot work:
///         <list type="bullet">
///             <item>The page renders (heading, generation form, job list, model manager).</item>
///             <item>With no models: the "install a model first" notice shows, and the model picker and the Generate
///             button are DISABLED — the UI never lets a request be sent that the node cannot serve.</item>
///             <item>The job list renders its empty state (no jobs exist on this node).</item>
///             <item>The model manager's three add-a-model tabs switch without a page error.</item>
///         </list>
///     </para>
///     <para>
///         POOLED: read-only. Nothing here mutates node state, and the two node-global reads it does make (installed
///         image models, image jobs) are empty for the whole run — no suite in this project installs an image model or
///         enqueues an image job, and there is no runtime that could create one.
///     </para>
/// </summary>
[Category("Page")]
public sealed class ImagesPageE2ETests : XEPooledE2ETestBase
{
    private async Task NavigateAndWaitForImagesPageAsync()
    {
        await Page.GotoAsync($"{NodeAppUrl}/images", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });

        // The route is capability-gated on nodeCapabilities.images (on by default). If it ever ships off, this
        // heading assertion fails loudly at the redirect rather than the test silently asserting the home page.
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions
            {
                Name = "Image Generation"
            }))
            .ToBeVisibleAsync();
    }

    [Test]
    [Category("Page")]
    public async Task Images_Page_Renders_And_Blocks_Generation_While_No_Model_Is_Installed()
    {
        var pageErrors = new List<string>();
        Page.PageError += (_, error) => pageErrors.Add(error);

        await NavigateAndWaitForImagesPageAsync();

        await Expect(Page.GetByTestId("image-generation-form")).ToBeVisibleAsync();

        // No installed models → the notice renders and both controls that could start a job are disabled.
        await Expect(Page.GetByTestId("image-form-no-models")).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
        {
            Timeout = 10_000
        });
        await Expect(Page.GetByTestId("image-form-submit")).ToBeDisabledAsync();
        await Expect(Page.GetByTestId("image-form-model")).ToBeDisabledAsync();

        // No jobs on this node → the job list settles into its empty state (not the loader, not a job card).
        await Expect(Page.GetByTestId("image-job-list-empty")).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
        {
            Timeout = 10_000
        });
        await Expect(Page.GetByTestId("image-job-card")).ToHaveCountAsync(0);

        // The model manager renders its own empty state rather than an error.
        await Expect(Page.GetByTestId("image-model-manager")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("image-models-empty")).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
        {
            Timeout = 10_000
        });

        await Assert.That(pageErrors.Count == 0).IsTrue();
    }

    [Test]
    [Category("Page")]
    public async Task Images_ModelManager_Tabs_Switch_Without_A_Page_Error()
    {
        var pageErrors = new List<string>();
        Page.PageError += (_, error) => pageErrors.Add(error);

        await NavigateAndWaitForImagesPageAsync();

        await Expect(Page.GetByTestId("image-model-add-tabs")).ToBeVisibleAsync();

        // "Browse" reaches Hugging Face through an un-routed HttpClient in this host, so its own empty/error panel is
        // the expected outcome — what is asserted is that the tab MOUNTS (the panels use keepMounted={false}, so a
        // render crash in one of them would surface here and nowhere else).
        await Page.GetByTestId("image-model-tab-browse").ClickAsync();
        await Expect(Page.GetByTestId("image-model-browse")).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
        {
            Timeout = 10_000
        });

        // "Manual" is a pure form — it must render its repo field with no network at all.
        await Page.GetByTestId("image-model-tab-manual").ClickAsync();
        await Expect(Page.GetByTestId("image-model-download-repo")).ToBeVisibleAsync();

        // Back to the default tab: the curated catalog settles into one of its own states (list / empty / error) —
        // it is served by a code-owned catalog endpoint, so which one is not this test's business; that it settles is.
        await Page.GetByTestId("image-model-tab-catalog").ClickAsync();
        await Expect(Page
                     .Locator("[data-testid='image-model-catalog'], [data-testid='image-model-catalog-empty'], [data-testid='image-model-catalog-error']")
                     .First)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
            {
                Timeout = 10_000
            });

        await Assert.That(pageErrors.Count == 0).IsTrue();
    }
}
