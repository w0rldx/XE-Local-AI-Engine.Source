namespace XE_Local_AI_Engine.Tests.E2ETests.Tests;

using Microsoft.Playwright;
using XE_Local_AI_Engine.Tests.E2ETests.Common;

/// <summary>
///     Browser-driven E2E for the MCP servers page (<c>/mcp</c>) — the highest-priority no-e2e-at-all
///     route (gap analysis P0-1). Exercises the full config-CRUD round-trip:
///     <list type="bullet">
///         <item>Register server → fill <c>McpServerForm</c> (Stdio transport: name + command) → save.</item>
///         <item>The new server appears in <c>McpServerList</c> by name.</item>
///         <item>Delete (confirm dialog) → the row is gone.</item>
///     </list>
///     <para>
///         No FakeOllama scripting is needed: an MCP registration is persisted node config, not a model
///         call. The seeders that would pre-populate rows (<c>McpServerStartupConnector</c> and friends)
///         are <c>IHostedService</c>s removed by the E2E factory's <c>RemoveAll&lt;IHostedService&gt;()</c>,
///         so the list starts empty — but the test still asserts BY NAME (not row count) to stay robust if
///         that ever changes.
///     </para>
///     <para>
///         Redaction guard: MCP is a redaction-sensitive surface. The Stdio command carries a unique secret
///         marker; after save + delete the test asserts that marker never leaks into the rendered DOM (the
///         list shows the command, but the secret-flavoured env value must never appear). See
///         <see cref="Mcp_Server_Round_Trip_Does_Not_Leak_Secret_Into_List" />.
///     </para>
///     <para>
///         Locator strategy mirrors the existing CRUD tests: Mantine <c>TextInput</c> places its
///         <c>data-testid</c> on a wrapper, so the inner control is reached via <c>GetByPlaceholder</c>
///         (en.json: "pages.mcp.form.name.placeholder" = "Filesystem tools", command placeholder =
///         "/usr/bin/my-mcp-server"). The footer Save button (<c>mcp-form-submit</c>) and create button
///         (<c>mcp-create-button</c>) are stable testids.
///     </para>
/// </summary>
[Category("Page")]
public sealed class McpServersPageE2ETests : XEPooledE2ETestBase
{
    private const string NamePlaceholder = "Filesystem tools";
    private const string CommandPlaceholder = "/usr/bin/my-mcp-server";

    private async Task NavigateAndWaitForMcpPageAsync()
    {
        await Page.GotoAsync($"{NodeAppUrl}/mcp", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });

        // Page heading renders unconditionally (i18n "pages.mcp.title" = "MCP servers").
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions
            {
                Name = "MCP servers"
            }))
            .ToBeVisibleAsync();

        var createButton = Page.GetByTestId("mcp-create-button");
        await Expect(createButton).ToBeVisibleAsync();
        await Expect(createButton).ToBeEnabledAsync();
    }

    [Test]
    [Category("Page")]
    public async Task Mcp_Page_Renders_Heading_And_Register_Button()
    {
        await NavigateAndWaitForMcpPageAsync();

        // Register button label comes from "pages.mcp.createButton" = "Register server".
        await Expect(Page.GetByTestId("mcp-create-button")).ToBeVisibleAsync();
    }

    [Test]
    [Category("Page")]
    public async Task Mcp_Server_Create_Save_AppearsInList_Then_Delete_Removes_It()
    {
        var pageErrors = new List<string>();
        Page.PageError += (_, error) => pageErrors.Add(error);

        await NavigateAndWaitForMcpPageAsync();

        // A unique name so the assertion is robust under the shared PerTestSession DB (other tests/runs
        // may have left rows). The default transport is Stdio, so name + command are the required fields.
        var serverName = $"E2E MCP Server {Guid.NewGuid():N}";

        await Page.GetByTestId("mcp-create-button").ClickAsync();

        // The editor dialog opens. Its body carries data-testid="mcp-editor-card" and the form itself
        // "mcp-server-form".
        await Expect(Page.GetByTestId("mcp-editor-card")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("mcp-server-form")).ToBeVisibleAsync();

        // Fill the required fields. Mantine TextInput places the testid on a wrapper; the inner <input>
        // is reached via its placeholder. Name → "Filesystem tools", command → "/usr/bin/my-mcp-server".
        await Page.GetByPlaceholder(NamePlaceholder).FillAsync(serverName);
        await Page.GetByPlaceholder(CommandPlaceholder).FillAsync("/usr/bin/true");

        // Save → POST /api/local/v1/mcp/servers. Wait for the response so the assertion runs after persist.
        var createResponse = await Page.RunAndWaitForResponseAsync(async () => await Page.GetByTestId("mcp-form-submit").ClickAsync(),
            response => response.Url.Contains("/mcp/servers", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(response.Request.Method, "POST", StringComparison.OrdinalIgnoreCase),
            new PageRunAndWaitForResponseOptions
            {
                Timeout = 10_000
            });

        await Assert.That(createResponse.Status).IsEqualTo(201);

        // The new server appears in the list BY NAME (not by row count — seeders may pre-populate).
        var serverCell = Page.GetByRole(AriaRole.Cell, new PageGetByRoleOptions
        {
            Name = serverName,
            Exact = true
        });
        await Expect(serverCell).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
        {
            Timeout = 5000
        });

        // Delete: the row's delete control has aria-label "Delete {{name}}". Clicking it raises the
        // ConfirmProvider dialog; confirm with its "Delete" button.
        var deleteRowButton = Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
        {
            Name = $"Delete {serverName}"
        });
        await Expect(deleteRowButton).ToBeVisibleAsync();

        var deleteResponse = await Page.RunAndWaitForResponseAsync(async () =>
            {
                await deleteRowButton.ClickAsync();
                // Confirm dialog: exact "Delete" button (ConfirmProvider confirmationText = "Delete").
                await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
                {
                    Name = "Delete",
                    Exact = true
                }).ClickAsync();
            },
            response => response.Url.Contains("/mcp/servers/", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(response.Request.Method, "DELETE", StringComparison.OrdinalIgnoreCase),
            new PageRunAndWaitForResponseOptions
            {
                Timeout = 10_000
            });

        await Assert.That(deleteResponse.Status >= 200 && deleteResponse.Status < 300).IsTrue();

        // The row must be gone after the list re-renders.
        await Expect(serverCell).ToHaveCountAsync(count: 0, new LocatorAssertionsToHaveCountOptions
        {
            Timeout = 5000
        });

        // No page-level JS error during the full create/delete sequence.
        await Assert.That(pageErrors.Count == 0).IsTrue();
    }

    [Test]
    [Category("Page")]
    public async Task Mcp_Server_Round_Trip_Does_Not_Leak_Secret_Into_List()
    {
        await NavigateAndWaitForMcpPageAsync();

        var serverName = $"E2E Redaction MCP {Guid.NewGuid():N}";
        // A secret-flavoured marker placed in an env-var value: the LIST must never render env values
        // (redaction-sensitive surface), so this marker must be absent from the page body after save.
        var secretMarker = $"SUPER-SECRET-{Guid.NewGuid():N}";

        await Page.GetByTestId("mcp-create-button").ClickAsync();
        await Expect(Page.GetByTestId("mcp-server-form")).ToBeVisibleAsync();

        await Page.GetByPlaceholder(NamePlaceholder).FillAsync(serverName);
        await Page.GetByPlaceholder(CommandPlaceholder).FillAsync("/usr/bin/true");

        // Add one environment variable whose value is the secret marker. Mantine TextInput places its
        // data-testid directly on the inner <input>, so the testid is the input itself.
        await Page.GetByTestId("mcp-form-env-add").ClickAsync();
        await Page.GetByTestId("mcp-form-env-key-0").FillAsync("API_TOKEN");
        await Page.GetByTestId("mcp-form-env-value-0").FillAsync(secretMarker);

        await Page.RunAndWaitForResponseAsync(async () => await Page.GetByTestId("mcp-form-submit").ClickAsync(),
            response => response.Url.Contains("/mcp/servers", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(response.Request.Method, "POST", StringComparison.OrdinalIgnoreCase),
            new PageRunAndWaitForResponseOptions
            {
                Timeout = 10_000
            });

        // The row appears by name…
        await Expect(Page.GetByRole(AriaRole.Cell, new PageGetByRoleOptions
            {
                Name = serverName,
                Exact = true
            }))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
            {
                Timeout = 5000
            });

        // …but the secret env value must NOT be present anywhere in the rendered DOM.
        var bodyText = await Page.Locator("body").InnerTextAsync();
        await Assert.That(bodyText.Contains(secretMarker, StringComparison.Ordinal)).IsFalse();

        // Clean up so this row does not pollute count-based assertions elsewhere on the shared session.
        var deleteRowButton = Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
        {
            Name = $"Delete {serverName}"
        });
        await deleteRowButton.ClickAsync();
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
        {
            Name = "Delete",
            Exact = true
        }).ClickAsync();
        await Expect(Page.GetByRole(AriaRole.Cell, new PageGetByRoleOptions
            {
                Name = serverName,
                Exact = true
            }))
            .ToHaveCountAsync(count: 0, new LocatorAssertionsToHaveCountOptions
            {
                Timeout = 5000
            });
    }
}
