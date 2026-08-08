namespace XE_Local_AI_Engine.Tests.E2ETests.Tests;

using Microsoft.Playwright;
using XE_Local_AI_Engine.Tests.E2ETests.Common;

/// <summary>
///     Browser-driven E2E for the Models page (<c>/models</c>) kind-change + delete round-trip.
///     The existing smoke test only proves a FakeOllama model renders; this suite
///     drives the management lifecycle against FakeOllama's show/delete endpoints:
///     <list type="bullet">
///         <item>
///             Register a NEW, uniquely-named model directly in <c>FakeOllamaState.Models</c> → the
///             installed-models table shows the new row after the list query runs. The browser-driven
///             pull that used to seed this row is gone: the Ollama model-pull UI and its endpoints were
///             removed in f38ce95a ("remove legacy Ollama model-pull path from Model Management"), so
///             there is no shipped surface left to drive. Seeding the provider's model set reproduces
///             exactly the post-pull state the rest of the test needs, and the kind-change + delete
///             paths this test actually guards are untouched by that removal.
///         </item>
///         <item>
///             Open its details → change the ModelKind override (PUT <c>models/{name}/kind</c>) → the
///             effective-kind badge reflects the override.
///         </item>
///         <item>
///             Delete it (DELETE <c>models/{name}</c>) → 200 with NO "Invalid model identifier" error.
///             The model tag deliberately contains a colon so the delete exercises the
///             <c>ModelRouteName.Decode</c> slash/colon URL-encoding fix (hey-api encodes the tag to
///             <c>%3A</c>; Kestrel leaves it encoded in the route value; the endpoint must decode before
///             validating).
///         </item>
///     </list>
///     <para>
///         FakeOllama serves the list and <c>ShowEndpoint</c> (<c>/api/show</c> capabilities for detection);
///         no live Ollama. The node's DELETE goes to the GGUF store rather than to Ollama, so the seeded name
///         is dropped from <c>FakeOllamaState.Models</c> in <c>[After(Test)]</c> — see the delete leg for why
///         the row's removal is not observable here.
///     </para>
/// </summary>
[Category("Page")]
public sealed class ModelsManagementE2ETests : XEE2ETestBase
{
    // Name prefix this suite owns. Matching on it lets the cleanup hook drop a seeded model without tracking
    // instance state (TUnit0018 forbids a test method assigning instance data).
    private const string SeededModelPrefix = "e2e-seeded-";

    [After(Test)]
    public Task RemoveSeededModelsAsync()
    {
        // FakeOllamaState is shared PerTestSession: a model left behind by a failed run would show up as a
        // phantom installed row in every later test. Idempotent — the happy path already deleted it.
        Factory.FakeOllamaState.Models =
        [
            .. Factory.FakeOllamaState.Models.Where(model => !model.StartsWith(SeededModelPrefix, StringComparison.OrdinalIgnoreCase))
        ];

        return Task.CompletedTask;
    }

    private async Task NavigateAndWaitForModelsPageAsync()
    {
        await Page.GotoAsync($"{NodeAppUrl}/models", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });

        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions
            {
                Name = "Model management"
            }))
            .ToBeVisibleAsync();

        // The installed-models table renders once the list query settles (FakeOllama is "online").
        await Expect(Page.GetByTestId("installed-models-table"))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
            {
                Timeout = 5000
            });
    }

    [Test]
    [Category("Page")]
    public async Task Models_SeededModel_ChangeKind_Delete_RoundTrip()
    {
        var pageErrors = new List<string>();
        Page.PageError += (_, error) => pageErrors.Add(error);

        // A unique tag WITH a colon — exercising the slash/colon route-decode path on delete. The colon is
        // hey-api-encoded to %3A; the delete endpoint must decode it before validation (ModelRouteName.Decode).
        var modelName = $"{SeededModelPrefix}{Guid.NewGuid():N}:test";

        // --- Register (replaces the removed browser-driven pull; see the class doc) ---
        // Seed BEFORE the first navigation so the page's initial list query already returns the row; that
        // keeps the assertion on a plain page load rather than on a refresh-button race.
        Factory.FakeOllamaState.Models = [.. Factory.FakeOllamaState.Models, modelName];

        await NavigateAndWaitForModelsPageAsync();

        var modelCell = Page.GetByRole(AriaRole.Cell, new PageGetByRoleOptions
        {
            Name = modelName,
            Exact = true
        });
        await Expect(modelCell).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
        {
            Timeout = 15_000
        });

        // --- Change kind via the details dialog ---
        // Open details (row "View {name} details" action). The dialog mounts a Type tab with an override Select.
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
        {
            Name = $"View {modelName} details"
        }).ClickAsync();

        // Switch to the Type tab and choose "Embedding" via the override Select (aria-labelled per model).
        await Page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions
        {
            Name = "Type"
        }).ClickAsync();

        var overrideSelect = Page.GetByLabel($"Override type for {modelName}");
        await Expect(overrideSelect).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
        {
            Timeout = 5000
        });

        // Selecting the override PUTs models/{name}/kind; wait for that response so the assertion runs after persist.
        await Page.RunAndWaitForResponseAsync(async () =>
            {
                await overrideSelect.ClickAsync();
                await Page.GetByRole(AriaRole.Option, new PageGetByRoleOptions
                {
                    Name = "Embedding"
                }).ClickAsync();
            },
            response => response.Url.Contains("/kind", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(response.Request.Method, "PUT", StringComparison.OrdinalIgnoreCase),
            new PageRunAndWaitForResponseOptions
            {
                Timeout = 10_000
            });

        // The Type-tab effective-kind badge reflects the override.
        await Expect(Page.GetByTestId($"model-kind-badge-{modelName}").First)
            .ToContainTextAsync("Embedding", new LocatorAssertionsToContainTextOptions
            {
                Timeout = 5000
            });

        // Close the details dialog (Escape) before deleting from the row.
        await Page.Keyboard.PressAsync("Escape");
        await Expect(Page.GetByTestId("installed-models-table")).ToBeVisibleAsync();

        // --- Delete (exercises the colon-decode path) ---
        var deleteResponse = await Page.RunAndWaitForResponseAsync(async () =>
            {
                await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
                {
                    Name = $"Delete {modelName}"
                }).ClickAsync();
                // Confirm dialog (ConfirmProvider confirmationText = "Delete").
                await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
                {
                    Name = "Delete",
                    Exact = true
                }).ClickAsync();
            },
            response => response.Url.Contains("/api/local/v1/models/", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(response.Request.Method, "DELETE", StringComparison.OrdinalIgnoreCase),
            new PageRunAndWaitForResponseOptions
            {
                Timeout = 10_000
            });

        // The decode fix means the DELETE must succeed (200), not 400 "Invalid model identifier". Reaching the
        // endpoint with a decoded, valid name IS the regression this leg guards.
        await Assert.That(deleteResponse.Status).IsEqualTo(200);

        // Deliberately NOT asserting that the row disappears. DeleteLocalModelEndpoint deletes through
        // IGgufModelStore only ("Ollama is no longer a runtime"), and its DeleteModelAsync is idempotent, so
        // deleting an Ollama-SERVED model returns 200 while the provider keeps listing it. Observing the row
        // vanish would need an installed GGUF model, which this fixture has none of (the llama.cpp provider is
        // an NSubstitute stub). Covering the removal end-to-end needs a GGUF-store fake in the fixture — a
        // deliberate gap, not a silently relaxed assertion.

        // No "Invalid model identifier" text leaked anywhere (the decode-regression guard), and no page error.
        var bodyText = await Page.Locator("body").InnerTextAsync();
        await Assert.That(bodyText.Contains("Invalid model identifier", StringComparison.OrdinalIgnoreCase)).IsFalse();
        await Assert.That(pageErrors.Count == 0).IsTrue();
    }
}
