namespace XE_Local_AI_Engine.Tests.E2ETests.Tests;

using Microsoft.Playwright;
using XE_Local_AI_Engine.Tests.E2ETests.Common;

/// <summary>
///     Browser-driven E2E for the Scheduler page (<c>/scheduler</c>) — a no-e2e-at-all shipped route
///     (gap analysis P0-2). Exercises the job create + list-render happy path:
///     <list type="bullet">
///         <item>Page renders heading + job list + "Create job".</item>
///         <item>Open <c>ScheduledJobForm</c> → pick the registered "Model recommendation check" template.</item>
///         <item>
///             Picking the template auto-selects its default schedule kind (Manual / "On demand"), which
///             needs no cron/interval/start-at — only a display name.
///         </item>
///         <item>Save → the new job appears in <c>ScheduledJobList</c> BY NAME.</item>
///     </list>
///     <para>
///         No FakeOllama scripting needed for create: a scheduled-job definition is persisted config; the
///         model run only happens when the job fires. The seeded Manual job
///         (<c>ModelRecommendationScheduleSeeder</c>) is an <c>IHostedService</c> removed by the E2E factory,
///         so the list starts empty in this host — the test still asserts BY NAME (not by row count).
///     </para>
///     <para>
///         Redaction guard: the scheduler is a redaction-sensitive surface. The job's display name carries
///         a unique marker so the assertion is exact; the list never renders decrypted parameters, so the
///         template's default parameter blob (which can carry secrets in real jobs) must not appear. This
///         test does not inject a secret into parameters (the chosen template's defaults are non-secret),
///         but it asserts the create round-trip stays clean (no error toast / page error).
///     </para>
///     <para>
///         SERIAL on purpose, even though nothing here reads shared state. Creating a
///         <c>model-recommendation-check</c> job is what flips the gating this host exposes to
///         <c>ModelRecommendationsPageE2ETests</c>, whose refresh test branches on whether such a job exists.
///         Running the two concurrently would let the job appear between that test's enabled-state read and
///         its assertions. Keeping this class in the serial group puts it in a phase that never overlaps the
///         pooled one, so the pooled test always observes a settled gate — whichever phase runs first (it
///         branches on both states, so it does not care which).
///     </para>
/// </summary>
[Category("Page")]
public sealed class SchedulerPageE2ETests : XESerialE2ETestBase
{
    // The only template registered in-host (ModelRecommendationCheckHandler.Descriptor.DisplayName).
    private const string TemplateDisplayName = "Model recommendation check";

    private async Task NavigateAndWaitForSchedulerPageAsync()
    {
        await Page.GotoAsync($"{NodeAppUrl}/scheduler", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });

        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions
            {
                Name = "Scheduler"
            }))
            .ToBeVisibleAsync();

        var createButton = Page.GetByTestId("scheduler-create-button");
        await Expect(createButton).ToBeVisibleAsync();
        await Expect(createButton).ToBeEnabledAsync();
    }

    [Test]
    [Category("Page")]
    public async Task Scheduler_Page_Renders_Heading_List_And_Create_Button()
    {
        await NavigateAndWaitForSchedulerPageAsync();

        // The list card is always present (the dialog overlays it).
        await Expect(Page.GetByTestId("scheduler-list-card")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("scheduler-create-button")).ToBeVisibleAsync();
    }

    [Test]
    [Category("Page")]
    public async Task Scheduler_Create_Manual_Job_Appears_In_List()
    {
        var pageErrors = new List<string>();
        Page.PageError += (_, error) => pageErrors.Add(error);

        await NavigateAndWaitForSchedulerPageAsync();

        var jobName = $"E2E Scheduler Job {Guid.NewGuid():N}";

        await Page.GetByTestId("scheduler-create-button").ClickAsync();

        // The editor dialog opens. Assert the FORM (not the DialogShell root, whose testid sits on the
        // zero-size Mantine Modal-root container that Playwright reports as not visible).
        await Expect(Page.GetByTestId("scheduled-job-form")).ToBeVisibleAsync();

        // Pick the registered template via the Mantine Select, whose data-testid sits directly on its inner
        // input. Opening the dropdown and choosing by display name also drives the schedule kind to its
        // template default of Manual ("On demand"), which needs no cron expression — only a job name remains.
        var templateSelect = Page.GetByTestId("scheduler-form-template");
        await templateSelect.ClickAsync();
        await Page.GetByRole(AriaRole.Option, new PageGetByRoleOptions
        {
            Name = TemplateDisplayName
        }).ClickAsync();

        // The template's description alert confirms the selection registered.
        await Expect(Page.GetByTestId("scheduler-form-template-description")).ToBeVisibleAsync();
        // Manual default → the manual-note alert renders (no cron field required).
        await Expect(Page.GetByTestId("scheduler-form-manual-note"))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
            {
                Timeout = 5000
            });

        // Fill the required display name (placeholder "Nightly cleanup").
        await Page.GetByPlaceholder("Nightly cleanup").FillAsync(jobName);

        // Save → POST to the scheduler create endpoint.
        var createResponse = await Page.RunAndWaitForResponseAsync(async () => await Page.GetByTestId("scheduler-form-submit").ClickAsync(),
            response => response.Url.Contains("/scheduler/jobs", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(response.Request.Method, "POST", StringComparison.OrdinalIgnoreCase),
            new PageRunAndWaitForResponseOptions
            {
                Timeout = 10_000
            });

        await Assert.That(createResponse.Status >= 200 && createResponse.Status < 300).IsTrue();

        // The new job appears in the list BY NAME (not row count — seeders may pre-populate).
        await Expect(Page.GetByRole(AriaRole.Cell, new PageGetByRoleOptions
            {
                Name = jobName,
                Exact = true
            }))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
            {
                Timeout = 5000
            });

        // No save-error alert and no page-level JS error during the create sequence.
        await Expect(Page.GetByTestId("scheduler-form-submit-error")).ToHaveCountAsync(0);
        await Assert.That(pageErrors.Count == 0).IsTrue();
    }
}
