namespace XE_Local_AI_Engine.Tests.E2ETests.Tests;

using Microsoft.Playwright;
using XE_Local_AI_Engine.Tests.E2ETests.Common;

/// <summary>
///     Browser-driven E2E for the Models page (<c>/models</c>) pull + kind-change + delete round-trip
///     (gap analysis P1-2). The existing smoke test only proves a FakeOllama model renders; this suite
///     drives the full management lifecycle against FakeOllama's pull/show/delete endpoints:
///     <list type="bullet">
///         <item>Pull a NEW, uniquely-named model (FakeOllama's <c>PullEndpoint</c> streams progress and
///               appends it to its model set) → the installed-models table shows the new row.</item>
///         <item>Open its details → change the ModelKind override (PUT <c>models/{name}/kind</c>) → the
///               effective-kind badge reflects the override.</item>
///         <item>Delete it (DELETE <c>models/{name}</c>) → the row is gone, with NO "Invalid model
///               identifier" error. The model tag deliberately contains a colon so the delete exercises the
///               <c>ModelRouteName.Decode</c> slash/colon URL-encoding fix (hey-api encodes the tag to
///               <c>%3A</c>; Kestrel leaves it encoded in the route value; the endpoint must decode before
///               validating).</item>
///     </list>
///     <para>
///         FakeOllama covers every call: <c>PullEndpoint</c> (NDJSON progress + register), <c>ShowEndpoint</c>
///         (<c>/api/show</c> capabilities for detection), and <c>DeleteEndpoint</c> (remove). No live Ollama.
///         The pull/show/delete scripts are not mutated, so no <c>[After(Test)]</c> reset is needed.
///     </para>
/// </summary>
[Category("Page")]
public sealed class ModelsManagementE2ETests : XEE2ETestBase
{
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
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 5000 });
    }

    [Test]
    [Category("Page")]
    public async Task Models_Pull_ChangeKind_Delete_RoundTrip()
    {
        var pageErrors = new List<string>();
        Page.PageError += (_, error) => pageErrors.Add(error);

        await NavigateAndWaitForModelsPageAsync();

        // A unique tag WITH a colon — exercising the slash/colon route-decode path on delete. The colon is
        // hey-api-encoded to %3A; the delete endpoint must decode it before validation (ModelRouteName.Decode).
        var modelName = $"e2e-pull-{Guid.NewGuid():N}:test";

        // --- Pull ---
        await Page.GetByTestId("open-pull-dialog-button").ClickAsync();
        // Mantine TextInput places its data-testid directly on the inner <input>, and the field carries the
        // placeholder "orca-mini:latest" — target by placeholder (consistent with the other input fills here).
        var pullInput = Page.GetByPlaceholder("orca-mini:latest");
        await Expect(pullInput).ToBeVisibleAsync();
        await pullInput.FillAsync(modelName);

        // The pull streams NDJSON from FakeOllama; on success the dialog closes and the installed list
        // invalidates + refetches. Click Download and wait for the new row to appear by name.
        await Page.GetByTestId("download-model-button").ClickAsync();

        var modelCell = Page.GetByRole(AriaRole.Cell, new PageGetByRoleOptions
        {
            Name = modelName,
            Exact = true
        });
        await Expect(modelCell).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });

        // --- Change kind via the details dialog ---
        // Open details (row "View {name} details" action). The dialog mounts a Type tab with an override Select.
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
        {
            Name = $"View {modelName} details"
        }).ClickAsync();

        // Switch to the Type tab and choose "Embedding" via the override Select (aria-labelled per model).
        await Page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "Type" }).ClickAsync();

        var overrideSelect = Page.GetByLabel($"Override type for {modelName}");
        await Expect(overrideSelect).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 5000 });

        // Selecting the override PUTs models/{name}/kind; wait for that response so the assertion runs after persist.
        await Page.RunAndWaitForResponseAsync(
            async () =>
            {
                await overrideSelect.ClickAsync();
                await Page.GetByRole(AriaRole.Option, new PageGetByRoleOptions { Name = "Embedding" }).ClickAsync();
            },
            response => response.Url.Contains("/kind", StringComparison.OrdinalIgnoreCase)
                       && string.Equals(response.Request.Method, "PUT", StringComparison.OrdinalIgnoreCase),
            new PageRunAndWaitForResponseOptions { Timeout = 10_000 });

        // The Type-tab effective-kind badge reflects the override.
        await Expect(Page.GetByTestId($"model-kind-badge-{modelName}").First)
            .ToContainTextAsync("Embedding", new LocatorAssertionsToContainTextOptions { Timeout = 5000 });

        // Close the details dialog (Escape) before deleting from the row.
        await Page.Keyboard.PressAsync("Escape");
        await Expect(Page.GetByTestId("installed-models-table")).ToBeVisibleAsync();

        // --- Delete (exercises the colon-decode path) ---
        var deleteResponse = await Page.RunAndWaitForResponseAsync(
            async () =>
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
            new PageRunAndWaitForResponseOptions { Timeout = 10_000 });

        // The decode fix means the DELETE must succeed (200), not 400 "Invalid model identifier".
        await Assert.That(deleteResponse.Status).IsEqualTo(200);

        // The row must be gone after the list refetches.
        await Expect(modelCell).ToHaveCountAsync(0, new LocatorAssertionsToHaveCountOptions { Timeout = 5000 });

        // No "Invalid model identifier" text leaked anywhere (the decode-regression guard), and no page error.
        var bodyText = await Page.Locator("body").InnerTextAsync();
        await Assert.That(bodyText.Contains("Invalid model identifier", StringComparison.OrdinalIgnoreCase)).IsFalse();
        await Assert.That(pageErrors.Count == 0).IsTrue();
    }
}
