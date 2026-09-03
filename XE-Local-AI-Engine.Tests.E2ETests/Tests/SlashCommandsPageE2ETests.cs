namespace XE_Local_AI_Engine.Tests.E2ETests.Tests;

using Microsoft.Playwright;
using XE_Local_AI_Engine.Tests.E2ETests.Common;

/// <summary>
///     Browser-driven E2E for the node slash-command page (<c>/commands</c>) — a shipped route with no browser
///     coverage at all. Slash commands are user-authored prompts the chat composer resolves, so the whole feature is
///     persisted config; this suite drives its CRUD round-trip through the real form:
///     <list type="bullet">
///         <item>Create a command (name + description + prompt) → it appears in the list as <c>/name</c>.</item>
///         <item>Reopen it → the persisted prompt repopulates (create → store → fetch, not just a 2xx).</item>
///         <item>Edit the prompt + save → the new value round-trips.</item>
///         <item>Delete (confirmation-gated) → the row is gone.</item>
///     </list>
///     <para>
///         POOLED: the command name carries a <c>Guid</c> and every locator is scoped to that row, so a concurrent
///         sibling cannot change what is asserted. Built-in commands may or may not populate the list, which is why
///         nothing here asserts an empty state or a row count.
///     </para>
/// </summary>
[Category("Page")]
public sealed class SlashCommandsPageE2ETests : XEPooledE2ETestBase
{
    private const string CommandsPath = "/api/local/v1/automation/commands";

    private async Task NavigateAndWaitForCommandsPageAsync()
    {
        await Page.GotoAsync($"{NodeAppUrl}/commands", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });

        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions
            {
                Name = "Commands"
            }))
            .ToBeVisibleAsync();

        // The create button is disabled while the list loads (and at the 100-command capacity), so waiting for it to
        // be enabled is also the "list has settled" signal.
        var createButton = Page.GetByTestId("command-create-button");
        await Expect(createButton).ToBeEnabledAsync(new LocatorAssertionsToBeEnabledOptions
        {
            Timeout = 10_000
        });
    }

    [Test]
    [Category("Page")]
    public async Task Commands_Page_Renders_Heading_And_Create_Button()
    {
        await NavigateAndWaitForCommandsPageAsync();

        await Expect(Page.GetByTestId("command-create-button")).ToBeVisibleAsync();
    }

    [Test]
    [Category("Page")]
    public async Task Command_Create_Reopen_Edit_And_Delete_Round_Trip()
    {
        var pageErrors = new List<string>();
        Page.PageError += (_, error) => pageErrors.Add(error);

        await NavigateAndWaitForCommandsPageAsync();

        // Command-name rule: lowercase alphanumerics separated by single hyphens. A "N"-format Guid is lowercase hex.
        var commandName = $"e2e-{Guid.NewGuid():N}";
        var prompt = $"E2E command prompt {Guid.NewGuid():N}.";
        var updatedPrompt = $"E2E command prompt UPDATED {Guid.NewGuid():N}.";

        await Page.GetByTestId("command-create-button").ClickAsync();
        await Expect(Page.GetByTestId("command-form")).ToBeVisibleAsync();

        await Page.GetByTestId("command-form-name").FillAsync(commandName);
        await Page.GetByTestId("command-form-description").FillAsync("E2E slash command.");
        await Page.GetByTestId("command-form-prompt").FillAsync(prompt);

        var createResponse = await Page.RunAndWaitForResponseAsync(async () => await Page.GetByTestId("command-form-submit").ClickAsync(),
            response => response.Url.Contains(CommandsPath, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(response.Request.Method, "POST", StringComparison.OrdinalIgnoreCase),
            new PageRunAndWaitForResponseOptions
            {
                Timeout = 10_000
            });

        await Assert.That(createResponse.Status >= 200 && createResponse.Status < 300).IsTrue();

        var row = Page.GetByTestId($"command-row-{commandName}");
        await Expect(row).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
        {
            Timeout = 5000
        });
        // The list renders the invocation form, not the bare name.
        await Expect(row).ToContainTextAsync($"/{commandName}");

        await row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions
        {
            Name = "Edit",
            Exact = true
        }).ClickAsync();

        await Expect(Page.GetByTestId("command-form")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("command-form-prompt")).ToHaveValueAsync(prompt, new LocatorAssertionsToHaveValueOptions
        {
            Timeout = 5000
        });
        await Expect(Page.GetByTestId("command-form-name")).ToHaveValueAsync(commandName);

        await Page.GetByTestId("command-form-prompt").FillAsync(updatedPrompt);

        var updateResponse = await Page.RunAndWaitForResponseAsync(async () => await Page.GetByTestId("command-form-submit").ClickAsync(),
            response => response.Url.Contains(CommandsPath, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(response.Request.Method, "PUT", StringComparison.OrdinalIgnoreCase),
            new PageRunAndWaitForResponseOptions
            {
                Timeout = 10_000
            });

        await Assert.That(updateResponse.Status >= 200 && updateResponse.Status < 300).IsTrue();

        await row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions
        {
            Name = "Edit",
            Exact = true
        }).ClickAsync();
        await Expect(Page.GetByTestId("command-form-prompt")).ToHaveValueAsync(updatedPrompt, new LocatorAssertionsToHaveValueOptions
        {
            Timeout = 5000
        });
        // Nothing edited this time, so the editor closes without the discard confirmation.
        await Page.GetByTestId("command-form-cancel").ClickAsync();
        await Expect(Page.GetByTestId("command-form")).ToHaveCountAsync(0);

        var deleteResponse = await Page.RunAndWaitForResponseAsync(async () =>
            {
                await row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions
                {
                    Name = "Delete",
                    Exact = true
                }).ClickAsync();
                await Page.GetByTestId("confirm-accept").ClickAsync();
            },
            response => response.Url.Contains(CommandsPath, StringComparison.OrdinalIgnoreCase)
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
}
