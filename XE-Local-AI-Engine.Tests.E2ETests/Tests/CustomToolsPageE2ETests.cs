namespace XE_Local_AI_Engine.Tests.E2ETests.Tests;

using Microsoft.Playwright;
using XE_Local_AI_Engine.Tests.E2ETests.Common;

/// <summary>
///     Browser-driven E2E for the custom-tool library page (<c>/custom-tools</c>) — a shipped route with no browser
///     coverage at all. Custom tools are the highest-consequence operator surface on the node (an enabled tool runs
///     an HTTP call or a host program with the operator's access), so the flows guarded here are the full authoring
///     round-trip plus the two safety properties the UI owns:
///     <list type="bullet">
///         <item>Create an <c>HttpFetch</c> tool through the real form → 201 → it appears in the list.</item>
///         <item>Reopen it → the persisted values repopulate and a stored SECRET header value comes back MASKED.</item>
///         <item>Edit + enable + save → the change persists (the list's enabled-only "Host access" badge appears).</item>
///         <item>Delete → the row is gone.</item>
///         <item>Save is GATED on the danger acknowledgement, and nothing is POSTed while it is unticked.</item>
///     </list>
///     <para>
///         Why E2E and not a component test: the secret round-trip (write plaintext → store encrypted → read back the
///         <c>__secret_set__</c> sentinel → an unedited save keeps the stored value) only exists across the real
///         endpoint + service + database. A mocked-store test cannot observe it.
///     </para>
///     <para>
///         POOLED: every assertion is scoped to a <c>Guid</c>-suffixed tool name (and to the row id the create response
///         returns), so a concurrent sibling browser session cannot change anything asserted here. Nothing in this file
///         reads node-global state or a node-wide empty state.
///     </para>
///     <para>
///         On the acknowledgement gate: the shipped behaviour is that the footer Save button is DISABLED until the
///         checkbox is ticked (<c>CustomToolsPage</c> gates it on <c>isAcknowledged</c>), so the in-form
///         "You must acknowledge this to save." message is unreachable through the footer. The test therefore asserts
///         the gate that actually ships — disabled → enabled → disabled again, with zero create requests issued —
///         rather than an error string the UI never renders on this path.
///     </para>
///     <para>
///         On the node kill switch: <c>StoredNodeSettings.DefaultCustomToolsEnabled</c> is <c>false</c>, but it gates
///         <c>ICustomToolCatalog</c> resolution at CALL time (whether an agent may invoke a tool), not the CRUD
///         surface — the page renders no banner about it, so there is nothing here to assert. What the page does show
///         unconditionally is the host-danger banner, which this suite pins.
///     </para>
/// </summary>
[Category("Page")]
public sealed class CustomToolsPageE2ETests : XEPooledE2ETestBase
{
    private const string ToolsPath = "/api/local/v1/custom-tools";

    private async Task NavigateAndWaitForCustomToolsPageAsync()
    {
        await Page.GotoAsync($"{NodeAppUrl}/custom-tools", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });

        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions
            {
                Name = "Custom tools"
            }))
            .ToBeVisibleAsync();

        var createButton = Page.GetByTestId("custom-tool-create-button");
        await Expect(createButton).ToBeVisibleAsync();
        await Expect(createButton).ToBeEnabledAsync();
    }

    [Test]
    [Category("Page")]
    public async Task CustomTools_Page_Renders_Danger_Banner_And_A_Settled_List()
    {
        await NavigateAndWaitForCustomToolsPageAsync();

        // The host-execution warning is unconditional page furniture — it must never be behind a state branch.
        await Expect(Page.GetByTestId("custom-tools-danger-banner")).ToBeVisibleAsync();

        // The list settles into exactly one of its two success shapes. Asserting "empty" outright would be a
        // node-wide claim, which a pooled sibling creating its own tool may legitimately falsify.
        await Expect(Page.Locator("[data-testid='custom-tools-empty'], [data-testid='custom-tools-table']").First)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
            {
                Timeout = 10_000
            });

        // …and never into the load-error shape.
        await Expect(Page.GetByTestId("custom-tools-list-error")).ToHaveCountAsync(0);
    }

    [Test]
    [Category("Page")]
    public async Task CustomTool_Create_Reopen_Enable_And_Delete_Round_Trip()
    {
        var pageErrors = new List<string>();
        Page.PageError += (_, error) => pageErrors.Add(error);

        await NavigateAndWaitForCustomToolsPageAsync();

        // Slug rule (CustomToolValidation): lowercase alphanumerics + inner underscores, 1-50 chars. A "N"-format Guid
        // is lowercase hex, so this is both valid and unique per test.
        var slug = $"e2e_{Guid.NewGuid():N}";
        var description = $"E2E custom tool probe {Guid.NewGuid():N}.";
        var updatedDescription = $"E2E custom tool probe UPDATED {Guid.NewGuid():N}.";
        // Placed in a header marked SECRET: the read path must return the sentinel, never this value.
        var secretMarker = $"SUPER-SECRET-{Guid.NewGuid():N}";

        // --- Create ---
        await Page.GetByTestId("custom-tool-create-button").ClickAsync();
        await Expect(Page.GetByTestId("custom-tool-editor-card")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("custom-tool-form")).ToBeVisibleAsync();

        await Page.GetByTestId("custom-tool-form-name").FillAsync(slug);
        await Page.GetByTestId("custom-tool-form-description").FillAsync(description);

        // HttpFetch is the default kind, so only its editor is on screen and only its block is submitted.
        await Page.GetByTestId("custom-tool-form-http-url").FillAsync("https://api.example.com/weather?city=paris");

        await Page.GetByTestId("custom-tool-form-http-headers-add").ClickAsync();
        await Page.GetByTestId("custom-tool-form-http-headers-name-0").FillAsync("X-Api-Key");
        await Page.GetByTestId("custom-tool-form-http-headers-value-0").FillAsync(secretMarker);
        await Page.GetByTestId("custom-tool-form-http-headers-secret-0").CheckAsync();

        await Page.GetByTestId("custom-tool-form-acknowledge").CheckAsync();

        var createResponse = await Page.RunAndWaitForResponseAsync(async () => await Page.GetByTestId("custom-tool-form-submit").ClickAsync(),
            response => response.Url.Contains(ToolsPath, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(response.Request.Method, "POST", StringComparison.OrdinalIgnoreCase),
            new PageRunAndWaitForResponseOptions
            {
                Timeout = 10_000
            });

        await Assert.That(createResponse.Status).IsEqualTo(201);

        // The created view carries the id every row control is keyed by, so every later locator is exact rather than
        // a text match that a sibling test's row could satisfy.
        var created = await createResponse.JsonAsync();
        var toolId = created?.GetProperty("id").GetString();
        await Assert.That(string.IsNullOrWhiteSpace(toolId)).IsFalse();

        var row = Page.GetByTestId($"custom-tool-row-{toolId}");
        await Expect(row).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
        {
            Timeout = 5000
        });

        // A new tool is created DISABLED, so the enabled-only "Host access" badge must be absent…
        await Expect(Page.GetByTestId($"custom-tool-danger-{toolId}")).ToHaveCountAsync(0);
        // …and the secret header value must not be anywhere in the rendered list.
        var listBody = await Page.Locator("body").InnerTextAsync();
        await Assert.That(listBody.Contains(secretMarker, StringComparison.Ordinal)).IsFalse();

        // --- Reopen: persisted values repopulate, the secret comes back masked ---
        await Page.GetByTestId($"custom-tool-edit-{toolId}").ClickAsync();
        await Expect(Page.GetByTestId("custom-tool-form")).ToBeVisibleAsync();

        await Expect(Page.GetByTestId("custom-tool-form-name")).ToHaveValueAsync(slug, new LocatorAssertionsToHaveValueOptions
        {
            Timeout = 5000
        });
        await Expect(Page.GetByTestId("custom-tool-form-description")).ToHaveValueAsync(description);
        await Expect(Page.GetByTestId("custom-tool-form-http-url")).ToHaveValueAsync("https://api.example.com/weather?city=paris");
        await Expect(Page.GetByTestId("custom-tool-form-http-headers-name-0")).ToHaveValueAsync("X-Api-Key");

        // The stored secret is returned as the sentinel and rendered as an EMPTY input with a "stored" placeholder —
        // the plaintext never leaves the node, and an untouched save keeps it.
        await Expect(Page.GetByTestId("custom-tool-form-http-headers-value-0")).ToHaveValueAsync(string.Empty);
        var editorBody = await Page.Locator("body").InnerTextAsync();
        await Assert.That(editorBody.Contains(secretMarker, StringComparison.Ordinal)).IsFalse();

        // --- Edit + enable + save ---
        await Page.GetByTestId("custom-tool-form-description").FillAsync(updatedDescription);
        await Page.GetByTestId("custom-tool-form-enabled").CheckAsync();

        // The acknowledgement is deliberately NOT carried over from the stored tool (CustomToolMappers.toFormValues
        // resets it): editing a host-exec tool re-asks for the decision every time, so Save starts disabled here too.
        await Expect(Page.GetByTestId("custom-tool-form-acknowledge")).Not.ToBeCheckedAsync();
        await Expect(Page.GetByTestId("custom-tool-form-submit")).ToBeDisabledAsync();
        await Page.GetByTestId("custom-tool-form-acknowledge").CheckAsync();

        var updateResponse = await Page.RunAndWaitForResponseAsync(async () => await Page.GetByTestId("custom-tool-form-submit").ClickAsync(),
            response => response.Url.Contains(ToolsPath, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(response.Request.Method, "PUT", StringComparison.OrdinalIgnoreCase),
            new PageRunAndWaitForResponseOptions
            {
                Timeout = 10_000
            });

        await Assert.That(updateResponse.Status >= 200 && updateResponse.Status < 300).IsTrue();

        // The enabled state survived the round-trip: the list renders the "Host access" danger badge only for an
        // ENABLED tool, so its appearance is the server-side proof, not a local toggle echo.
        await Expect(Page.GetByTestId($"custom-tool-danger-{toolId}")).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
        {
            Timeout = 5000
        });

        // …and the edited description round-tripped too.
        await Page.GetByTestId($"custom-tool-edit-{toolId}").ClickAsync();
        await Expect(Page.GetByTestId("custom-tool-form-description")).ToHaveValueAsync(updatedDescription, new LocatorAssertionsToHaveValueOptions
        {
            Timeout = 5000
        });
        await Expect(Page.GetByTestId("custom-tool-form-enabled")).ToBeCheckedAsync();
        // Nothing was edited this time, so the editor closes without the discard confirmation.
        await Page.GetByTestId("custom-tool-form-cancel").ClickAsync();
        await Expect(Page.GetByTestId("custom-tool-form")).ToHaveCountAsync(0);

        // --- Delete ---
        var deleteResponse = await Page.RunAndWaitForResponseAsync(async () =>
            {
                await Page.GetByTestId($"custom-tool-delete-{toolId}").ClickAsync();
                // ConfirmProvider dialog; confirmationText = "Delete".
                await Page.GetByTestId("confirm-accept").ClickAsync();
            },
            response => response.Url.Contains(ToolsPath, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(response.Request.Method, "DELETE", StringComparison.OrdinalIgnoreCase),
            new PageRunAndWaitForResponseOptions
            {
                Timeout = 10_000
            });

        await Assert.That(deleteResponse.Status >= 200 && deleteResponse.Status < 300).IsTrue();

        await Expect(row).ToHaveCountAsync(count: 0, new LocatorAssertionsToHaveCountOptions
        {
            Timeout = 5000
        });

        await Assert.That(pageErrors.Count == 0).IsTrue();
    }

    [Test]
    [Category("Page")]
    public async Task CustomTool_Save_Is_Blocked_Until_The_Danger_Is_Acknowledged()
    {
        // Counts every create attempt that reaches the wire. The gate is only worth anything if an unacknowledged
        // form cannot persist, so "the button was disabled" is backed by "nothing was POSTed".
        var createRequests = 0;
        Page.Request += (_, request) =>
        {
            if (request.Url.Contains(ToolsPath, StringComparison.OrdinalIgnoreCase)
                && string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref createRequests);
            }
        };

        await NavigateAndWaitForCustomToolsPageAsync();

        await Page.GetByTestId("custom-tool-create-button").ClickAsync();
        await Expect(Page.GetByTestId("custom-tool-form")).ToBeVisibleAsync();

        // A complete, otherwise-valid definition: only the acknowledgement is missing.
        await Page.GetByTestId("custom-tool-form-name").FillAsync($"e2e_{Guid.NewGuid():N}");
        await Page.GetByTestId("custom-tool-form-description").FillAsync("E2E acknowledgement gate probe.");
        await Page.GetByTestId("custom-tool-form-http-url").FillAsync("https://api.example.com/health");

        var submit = Page.GetByTestId("custom-tool-form-submit");
        await Expect(submit).ToBeDisabledAsync();

        // Ticking the acknowledgement — and only that — unlocks Save.
        await Page.GetByTestId("custom-tool-form-acknowledge").CheckAsync();
        await Expect(submit).ToBeEnabledAsync();

        // Un-ticking re-locks it, so the gate tracks the checkbox rather than latching on first tick.
        await Page.GetByTestId("custom-tool-form-acknowledge").UncheckAsync();
        await Expect(submit).ToBeDisabledAsync();

        await Assert.That(createRequests).IsEqualTo(0);

        // Leave the editor closed so the dialog cannot overlay a later test's first click on this shared session.
        // The form is dirty, so the close path raises the discard confirmation.
        await Page.GetByTestId("custom-tool-form-cancel").ClickAsync();
        await Page.GetByTestId("confirm-accept").ClickAsync();
        await Expect(Page.GetByTestId("custom-tool-form")).ToHaveCountAsync(0);
    }
}
