namespace XE_Local_AI_Engine.Tests.E2ETests.Tests;

using Microsoft.Playwright;
using XE_Local_AI_Engine.Tests.E2ETests.Common;
using XE_Local_AI_Engine.Tests.E2ETests.Infrastructure;

/// <summary>
///     Browser coverage for optional progressive tutorials. The normal fixture seeds all tutorial keys completed;
///     these tests temporarily clear them to exercise the first-use invitations.
/// </summary>
public sealed class OnboardingTourE2ETests : XEE2ETestBase
{
    private ILocator WelcomeDialog => Page.GetByTestId("onboarding-welcome-dialog");

    [Before(Test)]
    public async Task ClearSeededTutorialStateAsync()
    {
        await Factory.SetAdminTutorialStateAsync(null);
    }

    [After(Test)]
    public async Task RestoreSeededTutorialStateAsync()
    {
        await Factory.SetAdminTutorialStateAsync(XENodeE2EWebApplicationFactory.CompletedMainAppTourState);
    }

    [Test]
    [Category("Onboarding")]
    public async Task QuickStart_WithSavedProgress_OffersResumeButNeverAutoStarts()
    {
        await Page.AddInitScriptAsync($$"""
            globalThis.localStorage.setItem('{{XENodeE2EWebApplicationFactory.TourProgressStorageKey}}', '{"format":1,"stepId":"chatInput"}');
            """);
        await Page.GotoAsync($"{NodeAppUrl}/", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        await Expect(WelcomeDialog).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("onboarding-welcome-start")).ToHaveTextAsync("Resume");
        await Expect(Page.Locator("[id^='react-joyride-step-']")).ToHaveCountAsync(0);
    }

    [Test]
    [Category("Onboarding")]
    public async Task QuickStart_StartsOnlyOnClick_AndPersistsVersionedStepId()
    {
        await Page.GotoAsync($"{NodeAppUrl}/", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await Expect(WelcomeDialog).ToBeVisibleAsync();
        await Expect(Page.Locator("[id^='react-joyride-step-']")).ToHaveCountAsync(0);

        await Page.GetByTestId("onboarding-welcome-start").ClickAsync();
        await Expect(Page.Locator("#react-joyride-step-0")).ToBeVisibleAsync();
        await Page.Locator("#react-joyride-step-0 [data-action='primary']").ClickAsync();

        var progress = await Page.EvaluateAsync<string?>($"() => globalThis.localStorage.getItem('{XENodeE2EWebApplicationFactory.TourProgressStorageKey}')");
        await Assert.That(progress).IsNotNull();
        await Assert.That(progress ?? string.Empty).Contains("\"format\":1");
        await Assert.That(progress ?? string.Empty).Contains("\"stepId\":");
    }

    [Test]
    [Category("Onboarding")]
    public async Task AgentsInvitation_NotNowPersistsSkipped_WithoutStartingTutorial()
    {
        await Page.GotoAsync($"{NodeAppUrl}/", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await Page.RunAndWaitForResponseAsync(
            async () => await Page.GetByTestId("onboarding-welcome-skip").ClickAsync(),
            response => response.Url.Contains("tutorial-state", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(response.Request.Method, "PUT", StringComparison.OrdinalIgnoreCase));

        await Page.GotoAsync($"{NodeAppUrl}/agents", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        var invitation = Page.GetByTestId("tutorial-invitation-agents-basics");
        await Expect(invitation).ToBeVisibleAsync();
        await Expect(Page.Locator("[id^='react-joyride-step-']")).ToHaveCountAsync(0);

        await Page.RunAndWaitForResponseAsync(
            async () => await invitation.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Not now" }).ClickAsync(),
            response => response.Url.Contains("tutorial-state", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(response.Request.Method, "PUT", StringComparison.OrdinalIgnoreCase));

        var persisted = await Factory.GetAdminTutorialStateAsync();
        await Assert.That(persisted ?? string.Empty).Contains("\"key\":\"agents-v1\"");
        await Assert.That(persisted ?? string.Empty).Contains("\"status\":\"skipped\"");
    }
}
