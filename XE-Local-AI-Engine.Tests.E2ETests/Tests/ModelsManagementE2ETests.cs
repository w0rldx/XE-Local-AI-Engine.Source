namespace XE_Local_AI_Engine.Tests.E2ETests.Tests;

using System.Text.Json;
using Microsoft.Playwright;
using XE_Local_AI_Engine.Tests.E2ETests.Common;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Browser-driven E2E for the Models page (<c>/models</c>) kind-change + delete round-trip.
///     The existing smoke test only proves a listed model renders; this suite drives the management lifecycle:
///     <list type="bullet">
///         <item>
///             Register a NEW, uniquely-named model directly in <c>FakeOpenAiGatewayState.Models</c>, which the
///             fixture's installed-GGUF store reads → the installed-models table shows the new row after the list
///             query runs. Seeding the list reproduces the post-download state the paths under test need.
///         </item>
///         <item>
///             Open its details → change the ModelKind override (PUT <c>models/{name}/kind</c>) → the endpoint
///             answers the override as the effective kind and the row's kind badge shows it.
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
///         The node's DELETE runs on the on-disk GGUF registry, so the seeded name is dropped from
///         <c>FakeOpenAiGatewayState.Models</c> in <c>[After(Test)]</c> — see the delete leg for why the row's
///         removal is not observable here.
///     </para>
/// </summary>
[Category("Page")]
public sealed class ModelsManagementE2ETests : XESerialE2ETestBase
{
    // Name prefix this suite owns. Matching on it lets the cleanup hook drop a seeded model without tracking
    // instance state (TUnit0018 forbids a test method assigning instance data).
    private const string SeededModelPrefix = "e2e-seeded-";

    [After(Test)]
    public Task RemoveSeededModelsAsync()
    {
        // The gateway state is shared PerTestSession: a model left behind would show up as a
        // phantom installed row in every later test.
        Factory.FakeGatewayState.Models =
        [
            .. Factory.FakeGatewayState.Models.Where(model => !model.Id.StartsWith(SeededModelPrefix, StringComparison.OrdinalIgnoreCase))
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

        // The installed-models table renders once the list query settles.
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

        // Seed BEFORE the first navigation so the page's initial list query already returns the row; that
        // keeps the assertion on a plain page load rather than on a refresh-button race.
        Factory.FakeGatewayState.Models = [.. Factory.FakeGatewayState.Models, FakeGatewayLocalModelProvider.Model(modelName)];

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

        // Open details (row "View {name} details" action). The dialog mounts a Type tab with an override Select.
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
        {
            Name = $"View {modelName} details"
        }).ClickAsync();

        // Switch to the Type tab and choose "Embedding" via the override Select (aria-labeled per model).
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
        var kindResponse = await Page.RunAndWaitForResponseAsync(async () =>
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

        // The override is persisted and answered as the effective kind.
        await Assert.That(kindResponse.Status).IsEqualTo(200);
        using (var kind = JsonDocument.Parse(await kindResponse.TextAsync()))
        {
            await Assert.That(kind.RootElement.GetProperty("kind").GetString()).IsEqualTo("Embedding");
        }

        // Close the details dialog (Escape) before deleting from the row.
        await Page.Keyboard.PressAsync("Escape");
        await Expect(Page.GetByTestId("installed-models-table")).ToBeVisibleAsync();

        // The operator sees it: the row's badge comes from the model list, so this proves the list reports the override.
        await Expect(Page.GetByTestId("installed-models-table").GetByTestId($"model-kind-badge-{modelName}"))
            .ToContainTextAsync("Embedding", new LocatorAssertionsToContainTextOptions
            {
                Timeout = 5000
            });

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

        // With the name decoded the DELETE must succeed (200), not 400 "Invalid model identifier". Reaching the
        // endpoint with a decoded, valid name IS the regression this leg guards.
        await Assert.That(deleteResponse.Status).IsEqualTo(200);

        // Deliberately NOT asserting that the row disappears. The deletion coordinator works on the on-disk GGUF
        // registry, which does not hold a name only the fixture's store lists, and it answers that absence with a 200.
        // Observing the row vanish would need a real GGUF on disk in this fixture — a deliberate gap, not a silently
        // relaxed assertion.

        // No "Invalid model identifier" text leaked anywhere (the decode-regression guard), and no page error.
        var bodyText = await Page.Locator("body").InnerTextAsync();
        await Assert.That(bodyText.Contains("Invalid model identifier", StringComparison.OrdinalIgnoreCase)).IsFalse();
        await Assert.That(pageErrors.Count == 0).IsTrue();
    }
}
