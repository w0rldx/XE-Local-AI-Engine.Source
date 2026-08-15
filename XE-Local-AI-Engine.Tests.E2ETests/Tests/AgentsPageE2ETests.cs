namespace XE_Local_AI_Engine.Tests.E2ETests.Tests;

using Microsoft.Playwright;
using XE_Local_AI_Engine.Tests.E2ETests.Common;

/// <summary>
///     Browser-driven E2E tests for the Agents page (<c>/agents</c>).
///     <para>
///         The primary regression guard is <see cref="AgentForm_TypingIntoInstructions_DoesNotCrash" />,
///         which reproduces the RC-blocking <c>TypeError: Cannot read properties of null (reading 'value')</c>
///         that unit tests structurally cannot catch.
///     </para>
///     <para>
///         Root cause: React 19 nulls <c>event.currentTarget</c> after the synchronous handler returns.
///         When the value was read inside a <em>deferred</em> functional state updater
///         (<c>setValues((c) =&gt; ({ ...c, field: event.currentTarget.value }))</c>)
///         the updater ran after the null-out, crashing the route. The fix captures the value
///         into a local <c>const</c> before the updater. <c>FillAsync</c> sets the full value in one
///         browser event, but <c>PressSequentiallyAsync</c> fires one <c>input</c> event per keystroke,
///         matching the batched-render timing that triggers the null-ref in a real browser.
///     </para>
///     <para>
///         Locator strategy: Mantine's <c>Textarea</c> places <c>data-testid</c> on its wrapper
///         <c>&lt;div&gt;</c>, not the inner <c>&lt;textarea&gt;</c>.  The inner element is targeted
///         with a CSS descendant selector: <c>[data-testid="agent-form-instructions"] textarea</c>.
///         For deterministic navigation, the test waits for <c>POST /api/local/v1/agents</c> to settle
///         before asserting the resulting row.
///     </para>
/// </summary>
[Category("Page")]
public sealed class AgentsPageE2ETests : XEPooledE2ETestBase
{
    // Mantine 9 TextInput puts data-testid directly on the <input> element (unlike Textarea which
    // puts it on the wrapper div). Locate Name by its placeholder to match Mantine's actual DOM output.
    // For Textarea (Instructions), Mantine puts data-testid on the wrapper div; the inner element
    // is a <textarea> child — use GetByPlaceholder which resolves the <textarea> directly.
    private const string NameInputPlaceholder = "Research assistant";
    private const string InstructionsTextareaPlaceholder = "You are a helpful assistant that…";

    /// <summary>
    ///     Navigates to /agents and waits until the "New agent" button is visible and enabled.
    /// </summary>
    private async Task NavigateAndWaitForAgentsPageAsync()
    {
        await Page.GotoAsync($"{NodeAppUrl}/agents", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });

        // The create button is rendered unconditionally when the editor is closed.
        var createButton = Page.GetByTestId("agent-create-button");
        await Expect(createButton).ToBeVisibleAsync();
        await Expect(createButton).ToBeEnabledAsync();
    }

    [Test]
    [Category("Page")]
    public async Task Agents_Page_Renders_Heading_And_Create_Button()
    {
        await Page.GotoAsync($"{NodeAppUrl}/agents", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });

        // Page heading rendered unconditionally (i18n key "pages.agents.title" = "Agent definitions").
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions
            {
                Name = "Agent definitions"
            }))
            .ToBeVisibleAsync();

        // Create button is visible when the editor is not open.
        await Expect(Page.GetByTestId("agent-create-button")).ToBeVisibleAsync();
    }

    /// <summary>
    ///     Regression guard for the RC bug: <c>event.currentTarget.value</c> read inside a deferred
    ///     functional state updater → null-ref under React 19 batched rendering.
    ///     <para>
    ///         <c>PressSequentiallyAsync</c> fires one <c>input</c> event per keystroke, reproducing
    ///         the exact per-character onChange → batched-re-render → deferred-updater timing that
    ///         crashed the route in the live browser.  <c>FillAsync</c> does not reproduce this because
    ///         it dispatches a single synthetic input event and bypasses React's per-keystroke batching.
    ///     </para>
    /// </summary>
    [Test]
    [Category("Page")]
    public async Task AgentForm_TypingIntoInstructions_DoesNotCrash()
    {
        // Track page-level JS errors — a null-ref crash throws inside React and surfaces as a
        // pageerror before React's error boundary catches and re-renders the "Something went wrong" UI.
        var pageErrors = new List<string>();
        Page.PageError += (_, error) => pageErrors.Add(error);

        await NavigateAndWaitForAgentsPageAsync();

        // Open the "New agent" editor.
        await Page.GetByTestId("agent-create-button").ClickAsync();

        // The editor card must appear.
        await Expect(Page.GetByTestId("agent-editor-card")).ToBeVisibleAsync();

        // The form itself must be present — if it is absent after click the route already crashed.
        var form = Page.GetByTestId("agent-definition-form");
        await Expect(form).ToBeVisibleAsync();

        // --- Fill Name (TextInput) by placeholder ---
        // Mantine 9 TextInput puts data-testid on the <input> element, but GetByPlaceholder is
        // the most reliable cross-version approach and matches the pattern used by ChatPageE2ETests.
        // The Name field also uses a functional updater; filling it exercises that same path and
        // ensures the form passes validation so we can reach the submit assertion.
        var nameInput = Page.GetByPlaceholder(NameInputPlaceholder);
        await Expect(nameInput).ToBeVisibleAsync();
        await nameInput.PressSequentiallyAsync("RC Regression Agent", new LocatorPressSequentiallyOptions
        {
            Delay = 20 // 20 ms between keystrokes — enough for React to batch and flush
        });

        // --- Type into Instructions (autosize Textarea) via placeholder ---
        // GetByPlaceholder resolves the inner <textarea> directly (same as GetByPlaceholder on the
        // chat input, documented in ChatPageE2ETests). This is the primary crash site: each
        // keystroke fires onChange → setValues((c) => ({ ...c, instructions: event.currentTarget.value }))
        // where currentTarget was null in the buggy version.
        var instructionsTextarea = Page.GetByPlaceholder(InstructionsTextareaPlaceholder);
        await Expect(instructionsTextarea).ToBeVisibleAsync();
        await instructionsTextarea.PressSequentiallyAsync("You are a helpful regression-test agent. This text was typed keystroke-by-keystroke.",
            new LocatorPressSequentiallyOptions
            {
                Delay = 20
            });

        // The form must still be alive — the error boundary replaces it with "Something went wrong"
        // if any onChange handler threw during the typing sequence above.
        await Expect(form).ToBeVisibleAsync();

        // No page-level JS error must have fired during typing.
        await Assert.That(pageErrors.Count == 0).IsTrue();

        // --- Submit and assert the agent was created ---
        // Wait for the POST /api/local/v1/agents response; 10 s is ample for an in-process call.
        var createResponse = await Page.RunAndWaitForResponseAsync(async () => await Page.GetByTestId("agent-form-submit").ClickAsync(),
            response => response.Url.Contains("/api/local/v1/agents", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(response.Request.Method, "POST", StringComparison.OrdinalIgnoreCase),
            new PageRunAndWaitForResponseOptions
            {
                Timeout = 10_000
            });

        // The API must have accepted the create request (201 Created).
        await Assert.That(createResponse.Status).IsEqualTo(201);

        // After a successful save the editor closes and the list re-renders.
        // At least one agent row must appear in the table.
        await Expect(Page.Locator("[data-testid^='agent-definition-row-']").First)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
            {
                Timeout = 5000
            });

        // The create button must be visible again (editor closed = no crash / no stuck state).
        await Expect(Page.GetByTestId("agent-create-button")).ToBeVisibleAsync();

        // Final check: no page errors at all during the full create sequence.
        await Assert.That(pageErrors.Count == 0).IsTrue();
    }
}
