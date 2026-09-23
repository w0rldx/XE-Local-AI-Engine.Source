namespace XE_Local_AI_Engine.Tests.E2ETests.Tests;

using Microsoft.Playwright;
using XE_Local_AI_Engine.Tests.E2ETests.Common;

/// <summary>
///     Browser-driven E2E for the loaded-models page (<c>/loaded-models</c>). The page lists the llama.cpp running
///     models the process supervisor reports; the Ollama in-memory section was removed on 2026-09-23 by operator
///     decision, so this suite also pins that it stays gone. No llama-server runs in this host, and the running-models
///     endpoint is defined to degrade to an OK-empty list rather than error, so the panel's empty state is the
///     deterministic expectation.
///     <para>
///         SERIAL: the empty-panel assertion is a node-wide empty state, and the panel also surfaces an in-flight GGUF
///         download as a running entry, so a pooled sibling could populate it mid-assertion.
///     </para>
/// </summary>
[Category("Page")]
public sealed class LoadedModelsPageE2ETests : XESerialE2ETestBase
{
    [Test]
    [Category("Page")]
    public async Task LoadedModels_Page_Renders_The_LlamaCpp_Panel_And_No_Ollama_Section()
    {
        var pageErrors = new List<string>();
        Page.PageError += (_, error) => pageErrors.Add(error);

        await Page.GotoAsync($"{NodeAppUrl}/loaded-models", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });

        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions
            {
                Name = "Loaded models"
            }))
            .ToBeVisibleAsync();

        await Expect(Page.GetByTestId("loaded-models-llamacpp-card")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("loaded-models-llamacpp-empty")).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
        {
            Timeout = 15_000
        });
        await Expect(Page.GetByTestId("loaded-models-llamacpp-table")).ToHaveCountAsync(0);

        // The removed Ollama section rendered one of these ids in every state; none may come back.
        await Expect(Page.GetByTestId("loaded-models-table")).ToHaveCountAsync(0);
        await Expect(Page.Locator("[data-testid='loaded-models-empty'], [data-testid='loaded-models-unavailable'], [data-testid='loaded-models-error']"))
            .ToHaveCountAsync(0);

        await Assert.That(pageErrors.Count == 0).IsTrue();
    }
}
