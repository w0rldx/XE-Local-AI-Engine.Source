namespace XE_Local_AI_Engine.Tests.E2ETests.Tests;

using Microsoft.Playwright;
using XE_Local_AI_Engine.Testing.FakeOllama;
using XE_Local_AI_Engine.Tests.E2ETests.Common;

/// <summary>
///     Browser-driven E2E for the loaded-models page (<c>/loaded-models</c>) — a shipped route with no browser
///     coverage at all. The page shows two DIFFERENT runtimes side by side and this suite covers both:
///     <list type="bullet">
///         <item>
///             Ollama in-memory list, driven from FakeOllama's <c>/api/ps</c>: the empty state with nothing loaded,
///             then a real row when the fake reports a loaded model, then the confirmation gate in front of eject.
///             The eject REQUEST itself is not asserted — it cannot currently reach the wire at all; see the defect
///             note on <see cref="LoadedModels_Lists_A_Reported_Model_And_Eject_Is_Confirmation_Gated" />.
///         </item>
///         <item>
///             The llama.cpp running-models panel, which derives from the process supervisor. No llama-server runs in
///             this host, and its endpoint is defined to degrade to an OK-empty list rather than error, so the panel's
///             empty state is the deterministic expectation.
///         </item>
///     </list>
///     <para>
///         SERIAL, and it must stay serial: both tests write <c>FakeOllamaState.RunningModels</c>, which is ONE shared
///         object on the <c>PerTestSession</c> host that every concurrent browser session observes. A pooled sibling
///         polling this same endpoint would see another test's injected model. The <c>[After(Test)]</c> hook clears
///         the list again so the fake never leaks a loaded model into a later suite.
///     </para>
/// </summary>
[Category("Page")]
public sealed class LoadedModelsPageE2ETests : XESerialE2ETestBase
{
    // A model FakeOllama already advertises, so the unload endpoint's ModelNameValidator sees a normal name:tag.
    private const string LoadedModelName = "qwen3.5:0.8b";

    [After(Test)]
    public void ResetFakeOllamaRunningModels()
    {
        // Never leave an injected in-memory model behind: this state is shared for the whole session.
        Factory.FakeOllamaState.RunningModels = [];
    }

    private async Task NavigateAndWaitForLoadedModelsPageAsync()
    {
        await Page.GotoAsync($"{NodeAppUrl}/loaded-models", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });

        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions
            {
                Name = "Loaded models"
            }))
            .ToBeVisibleAsync();
    }

    [Test]
    [Category("Page")]
    public async Task LoadedModels_Page_Renders_Both_Runtimes_Empty_When_Nothing_Is_Loaded()
    {
        Factory.FakeOllamaState.RunningModels = [];

        var pageErrors = new List<string>();
        Page.PageError += (_, error) => pageErrors.Add(error);

        await NavigateAndWaitForLoadedModelsPageAsync();

        // The Ollama section settles into one of its two "nothing loaded" shapes — reachable-but-empty or
        // provider-unavailable. Which one depends on whether the fake transport answers /api/ps, and both are
        // legitimate no-models states; what must NOT appear is the error alert or a populated table.
        await Expect(Page.Locator("[data-testid='loaded-models-empty'], [data-testid='loaded-models-unavailable']").First)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
            {
                Timeout = 15_000
            });
        await Expect(Page.GetByTestId("loaded-models-error")).ToHaveCountAsync(0);
        await Expect(Page.GetByTestId("loaded-models-table")).ToHaveCountAsync(0);

        // No llama-server process is supervised in this host, and the endpoint degrades to OK-empty on any supervisor
        // failure, so the panel's empty state is deterministic here.
        await Expect(Page.GetByTestId("loaded-models-llamacpp-card")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("loaded-models-llamacpp-empty")).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
        {
            Timeout = 15_000
        });
        await Expect(Page.GetByTestId("loaded-models-llamacpp-table")).ToHaveCountAsync(0);

        await Assert.That(pageErrors.Count == 0).IsTrue();
    }

    /// <summary>
    ///     Covers the reported-model row and the confirmation gate in front of eject.
    ///     <para>
    ///         It deliberately stops at the confirmation and does NOT assert the unload request, because on this build
    ///         that request is never sent. LIVE DEFECT (found by this suite, not a test limitation):
    ///         <c>useEjectModel</c> posts <c>body: {} as never</c> to dodge the FastEndpoints 415 on a route-only POST,
    ///         but the generated SDK's <c>requestValidator</c> for <c>unloadLocalModel</c> is
    ///         <c>z.object({ body: z.never().optional(), … })</c>, which rejects <c>{}</c> — so the client throws
    ///         before <c>buildUrl</c> and the operator always gets the "Could not eject the model." toast. Verified by
    ///         parsing that exact schema with that exact payload. The llama.cpp eject beside it is unaffected (it posts
    ///         a real body).
    ///     </para>
    ///     <para>
    ///         follow-up: once the body/validator mismatch is fixed, extend this test to wait for the
    ///         <c>models/{modelName}/unload</c> POST and assert a 2xx — the flow up to the confirmation is already
    ///         driven here, so only the response wait needs adding.
    ///     </para>
    /// </summary>
    [Test]
    [Category("Page")]
    public async Task LoadedModels_Lists_A_Reported_Model_And_Eject_Is_Confirmation_Gated()
    {
        // FakeOllama's /api/ps now reports one resident model, so the page renders the real table path.
        Factory.FakeOllamaState.RunningModels =
        [
            new FakeOllamaState.FakeOllamaRunningModel(
                LoadedModelName,
                DateTimeOffset.UtcNow.AddMinutes(5),
                SizeBytes: 900_000_000,
                SizeVramBytes: 800_000_000)
        ];

        var pageErrors = new List<string>();
        Page.PageError += (_, error) => pageErrors.Add(error);

        await NavigateAndWaitForLoadedModelsPageAsync();

        await Expect(Page.GetByTestId("loaded-models-table")).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
        {
            Timeout = 15_000
        });
        await Expect(Page.GetByTestId($"loaded-models-row-{LoadedModelName}")).ToBeVisibleAsync();

        var ejectButton = Page.GetByTestId($"loaded-models-eject-{LoadedModelName}");
        await Expect(ejectButton).ToBeEnabledAsync();

        // Eject is confirmation-gated: freeing memory the runtime is holding can interrupt work, so it must never be a
        // single-click action. Clicking the row control opens the confirmation instead of acting.
        await ejectButton.ClickAsync();

        // The confirm dialog's accept button carries a stable testid: its LABEL here is also "Eject", which would
        // otherwise match the row button that opened it.
        var confirmAccept = Page.GetByTestId("confirm-accept");
        await Expect(confirmAccept).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
        {
            Timeout = 10_000
        });
        // The dismissive option is present too, so the dialog is a real choice rather than an acknowledgement.
        await Expect(Page.GetByTestId("confirm-cancel")).ToBeVisibleAsync();

        // Dismiss rather than accept — see the defect note on this method for why accepting cannot currently be
        // asserted end to end. Cancelling must leave the row exactly where it was.
        await Page.GetByTestId("confirm-cancel").ClickAsync();
        await Expect(confirmAccept).ToHaveCountAsync(0);
        await Expect(Page.GetByTestId($"loaded-models-row-{LoadedModelName}")).ToBeVisibleAsync();

        await Assert.That(pageErrors.Count == 0).IsTrue();
    }
}
