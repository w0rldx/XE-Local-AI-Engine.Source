namespace XE_Local_AI_Engine.Tests.E2ETests.Tests;

using Microsoft.Playwright;
using XE_Local_AI_Engine.Tests.E2ETests.Common;
using XE_Local_AI_Engine.Tests.E2ETests.Infrastructure;

/// <summary>
///     Coverage for the opt-in first-run onboarding tour — the ONE flow the rest of the suite
///     deliberately does not see, because <see cref="XENodeE2EWebApplicationFactory" /> seeds the admin
///     as a returning user (see <see cref="XENodeE2EWebApplicationFactory.CompletedMainAppTourState" />).
///     <para>
///         Each test here clears that seeded state in a <c>[Before(Test)]</c> hook so the single node
///         admin becomes a genuine first-run user, and restores it in <c>[After(Test)]</c>. That mutation
///         is safe because browser E2E runs strictly sequentially (<c>BrowserParallelLimit.Limit == 1</c>),
///         so no other test can observe the window in which the prompt is armed.
///     </para>
///     <para>
///         The node is single-user by design (login is password-only — see <c>Login.tsx</c> and
///         <c>NodeAuthService.ResolveLoginUserAsync</c>, which resolves THE setup-completed user), so a
///         "second, brand-new user" is not reachable through the product. Toggling the one admin's
///         persisted tour state is the only honest way to exercise the first-run path.
///     </para>
/// </summary>
public sealed class OnboardingTourE2ETests : XEE2ETestBase
{
    // Step 0 of the main app tour ("navModels"). Asserted verbatim so a silently reworded / re-ordered
    // first step surfaces here rather than making the tour quietly start somewhere else.
    private const string FirstStepTitle = "Find your models here";

    // Namespaced localStorage key the provider writes the in-progress step index to on every advance
    // (TOUR_PROGRESS_STORAGE_KEY in useTourState.ts). Reading it proves the tour really moved on. Taken from the
    // factory rather than re-flattened here so the tour key has exactly one C# home, guarded against the
    // TypeScript source by XE-Local-AI-Engine.Tests/Onboarding/OnboardingTourKeyDriftTests.
    private const string TourProgressStorageKey = XENodeE2EWebApplicationFactory.TourProgressStorageKey;

    // The `data-testid` now lands on the Modal's CONTENT section, which is the `role="dialog"` element itself — it has a
    // real box when open and is unmounted when closed, so this single locator answers both "is it up" and "did it stay
    // away". It used to land on Mantine's Modal ROOT, a zero-box portal wrapper Playwright always reports as hidden even
    // while the dialog is on screen, and this locator had to chain `.GetByRole(AriaRole.Dialog)` to descend into the real
    // element. DialogShell routes the id for every dialog now (via Mantine's `attributes` Styles API), so that descent
    // would find nothing and had to go. Pinned by DialogShell.test.tsx, which asserts the tagged element is both
    // `mantine-Modal-content` and `role="dialog"`.
    private ILocator WelcomeDialog => Page.GetByTestId("onboarding-welcome-dialog");

    [Before(Test)]
    public async Task ClearSeededTutorialStateAsync()
    {
        await Factory.SetAdminTutorialStateAsync(null);
    }

    [After(Test)]
    public async Task RestoreSeededTutorialStateAsync()
    {
        // Unconditional: leaving the admin in the first-run state would put the welcome modal back over
        // every subsequent test in the run.
        await Factory.SetAdminTutorialStateAsync(XENodeE2EWebApplicationFactory.CompletedMainAppTourState);
    }

    [Test]
    [Category("Onboarding")]
    public async Task Welcome_Prompt_Appears_For_First_Run_User()
    {
        await Page.GotoAsync($"{NodeAppUrl}/", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });

        await Expect(WelcomeDialog).ToBeVisibleAsync();
        // Substring of onboarding.welcome.title; the leading "Welcome —" is dropped so the assertion does not
        // hinge on the em dash surviving translation-file edits.
        await Expect(Page.GetByText("let's get you to your first answer")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("onboarding-welcome-start")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("onboarding-welcome-skip")).ToBeVisibleAsync();
    }

    [Test]
    [Category("Onboarding")]
    public async Task Welcome_Skip_Persists_And_Does_Not_Reprompt_On_Reload()
    {
        await Page.GotoAsync($"{NodeAppUrl}/", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });

        await Expect(WelcomeDialog).ToBeVisibleAsync();

        // Skip fires a fire-and-forget PUT. Await the response rather than the closed dialog: the dialog closes
        // from local state and the reload below would otherwise be free to abort the in-flight write, turning a
        // real persistence assertion into a race.
        await Page.RunAndWaitForResponseAsync(async () => await Page.GetByTestId("onboarding-welcome-skip").ClickAsync(),
            response => response.Url.Contains("tutorial-state", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(response.Request.Method, "PUT", StringComparison.OrdinalIgnoreCase));

        await Expect(WelcomeDialog).ToBeHiddenAsync();

        // A full reload discards every scrap of client state (the provider's promptHandledRef included), so
        // the dialog can only stay closed if the SKIP actually reached the server and the refetched
        // tutorial state carries a terminal entry.
        await Page.ReloadAsync(new PageReloadOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });

        // Assert the app is really up before asserting an absence, so a blank page cannot pass this test.
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions
            {
                Name = "Welcome!"
            }))
            .ToBeVisibleAsync();
        await Expect(WelcomeDialog).ToBeHiddenAsync();

        // The reload above already proves the read path; this pins the persisted shape on the identity row
        // so a serialization change cannot silently degrade the suppression to a client-only effect.
        var persisted = await Factory.GetAdminTutorialStateAsync();
        await Assert.That(persisted).IsNotNull();
        await Assert.That(persisted ?? string.Empty).Contains($"\"key\":\"{XENodeE2EWebApplicationFactory.MainAppTourKey}\"");
        await Assert.That(persisted ?? string.Empty).Contains("\"status\":\"skipped\"");
    }

    [Test]
    [Category("Onboarding")]
    public async Task Start_Tour_Opens_First_Step_And_Next_Advances()
    {
        await Page.GotoAsync($"{NodeAppUrl}/", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });

        await Expect(WelcomeDialog).ToBeVisibleAsync();
        await Page.GetByTestId("onboarding-welcome-start").ClickAsync();

        // react-joyride ids its floater `react-joyride-step-{index}` and its heading `joyride-tooltip-title`,
        // so both the position in the tour and the rendered copy are assertable without a test-only hook.
        await Expect(Page.Locator("#react-joyride-step-0")).ToBeVisibleAsync();
        await Expect(Page.Locator("#joyride-tooltip-title")).ToHaveTextAsync(FirstStepTitle);

        await Page.Locator("#react-joyride-step-0 [data-action='primary']").ClickAsync();

        // Step 1 (`recommendationInstall`) is route-bound: the provider navigates to the recommendations
        // page and Joyride then waits up to its own 3 s targetWaitTimeout for the lazily mounted target.
        // The wider budget covers that route load, not general flakiness.
        await Expect(Page.Locator("#react-joyride-step-1")).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
        {
            Timeout = 20_000
        });
        await Expect(Page.Locator("#react-joyride-step-0")).ToBeHiddenAsync();

        // The provider persists the live step index on every advance so a mid-tour reload resumes; reading it
        // back confirms the advance was a real state transition, not just a re-rendered tooltip.
        var persistedStepIndex = await Page.EvaluateAsync<string?>($"() => globalThis.localStorage.getItem('{TourProgressStorageKey}')");
        await Assert.That(persistedStepIndex).IsEqualTo("1");
    }
}
