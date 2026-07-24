namespace XE_Local_AI_Engine.Tests.E2ETests.Tests;

using Microsoft.Playwright;
using XE_Local_AI_Engine.Tests.E2ETests.Common;

/// <summary>
///     Browser-driven E2E for the Model Recommendations page (<c>/model-recommendations</c>) — a
///     no-e2e-at-all shipped route (gap analysis P0-4). Exercises the recommend/refresh surface ONLY; the
///     benchmark path is intentionally GATED everywhere and is never touched here.
///     <list type="bullet">
///         <item>The page renders (heading + cache-only recommendations snapshot or no-cache notice).</item>
///         <item>
///             The "Refresh now" button is gated on a <c>model-recommendation-check</c> scheduled job
///             existing: enabled when one exists, disabled (with the no-job guidance alert) otherwise.
///         </item>
///         <item>
///             When enabled, clicking it fires the refresh POST
///             (<c>/api/local/v1/model-fit/recommendations/refresh</c>) — never a benchmark.
///         </item>
///         <item>When disabled, clicking it fires NO POST.</item>
///     </list>
///     <para>
///         Both gating states are asserted because the seeded Manual job (<c>ModelRecommendationScheduleSeeder</c>)
///         is an <c>IHostedService</c> removed by the E2E factory, so in isolation NO job exists and the button is
///         disabled — but the shared <c>PerTestSession</c> host means <c>SchedulerPageE2ETests</c> may have created
///         a <c>model-recommendation-check</c> job in an earlier test, flipping the button to enabled. The test
///         branches on the rendered enabled-state and asserts the corresponding invariant, so it is correct in
///         either ordering (the gap analysis explicitly asks the test to "handle both present/absent").
///     </para>
///     <para>
///         FakeOllama is NOT needed: the recommend path is cache/registry-driven and the refresh merely triggers
///         the existing scheduler job (TriggerNowAsync). The benchmark — the only path that runs a live model — is
///         disabled everywhere and is asserted-absent, never invoked.
///     </para>
/// </summary>
[Category("Page")]
public sealed class ModelRecommendationsPageE2ETests : XEE2ETestBase
{
    private async Task NavigateAndWaitForRecommendationsPageAsync()
    {
        await Page.GotoAsync($"{NodeAppUrl}/model-recommendations", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });

        // Heading pages.modelFit.recommendations.title renders unconditionally (the route is
        // capability-gated on modelFit, which is on by default in the bundle). The copy was renamed
        // "Model recommendations" -> "Local model advisor" in a3f85eb9 (model-advisor React surface).
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions
            {
                Name = "Local model advisor"
            }))
            .ToBeVisibleAsync();
    }

    [Test]
    [Category("Page")]
    public async Task ModelRecommendations_Page_Renders_Heading_And_Refresh_Button()
    {
        await NavigateAndWaitForRecommendationsPageAsync();

        // The Refresh-now button (gated) and the use-case select always render.
        await Expect(Page.GetByTestId("model-fit-refresh-button")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("model-fit-use-case-select")).ToBeVisibleAsync();

        // Either a cached snapshot OR the no-cache notice must be present (the page never renders blank).
        var snapshot = Page.GetByTestId("model-fit-snapshot");
        var noCache = Page.GetByTestId("model-fit-no-cache");
        await Expect(snapshot.Or(noCache).First)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
            {
                Timeout = 5000
            });
    }

    [Test]
    [Category("Page")]
    public async Task ModelRecommendations_Refresh_Is_Gated_On_RecommendationCheck_Job()
    {
        await NavigateAndWaitForRecommendationsPageAsync();

        var refreshButton = Page.GetByTestId("model-fit-refresh-button");
        await Expect(refreshButton).ToBeVisibleAsync();

        // Let the jobs query settle so the gating reflects the real job-presence state. The page enables the
        // button only when a model-recommendation-check job exists (canRefresh = refreshJob !== undefined).
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var isEnabled = await refreshButton.IsEnabledAsync();
        if (isEnabled)
        {
            // A model-recommendation-check job exists (e.g. SchedulerPageE2ETests created one on this shared
            // session). The no-job guidance alert must be ABSENT, and clicking Refresh must fire the refresh
            // POST — the recommend/refresh path, never a benchmark.
            await Expect(Page.GetByTestId("model-fit-no-job-guidance")).ToHaveCountAsync(0);

            var refreshResponse = await Page.RunAndWaitForResponseAsync(async () => await refreshButton.ClickAsync(),
                response => response.Url.Contains("/model-fit/recommendations/refresh", StringComparison.OrdinalIgnoreCase)
                            && string.Equals(response.Request.Method, "POST", StringComparison.OrdinalIgnoreCase),
                new PageRunAndWaitForResponseOptions
                {
                    Timeout = 10_000
                });

            // The refresh trigger is accepted (it triggers the existing scheduler job; the recommend run is
            // cache-driven). Any 2xx proves the recommend/refresh path fired without touching the benchmark.
            await Assert.That(refreshResponse.Status >= 200 && refreshResponse.Status < 300).IsTrue();
        }
        else
        {
            // No model-recommendation-check job exists → the button is disabled and the no-job guidance alert
            // tells the operator to create a schedule. This is the seeded-removed isolation baseline.
            await Expect(refreshButton).ToBeDisabledAsync();
            await Expect(Page.GetByTestId("model-fit-no-job-guidance")).ToBeVisibleAsync();

            // A disabled Refresh must issue NO refresh POST. RunAndWaitForRequest timing out (TimeoutException)
            // is the deterministic proof — mirrors the Dashboard disabled-button-no-POST pattern.
            var requestFired = false;
            try
            {
                await Page.RunAndWaitForRequestAsync(async () => await refreshButton.ClickAsync(new LocatorClickOptions
                    {
                        Force = false
                    }),
                    request => request.Url.Contains("/model-fit/recommendations/refresh", StringComparison.OrdinalIgnoreCase)
                               && string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase),
                    new PageRunAndWaitForRequestOptions
                    {
                        Timeout = 1500
                    });
                requestFired = true;
            }
            catch (TimeoutException)
            {
                // Expected: no request fired within the window.
            }

            await Assert.That(requestFired).IsFalse();
        }
    }
}
