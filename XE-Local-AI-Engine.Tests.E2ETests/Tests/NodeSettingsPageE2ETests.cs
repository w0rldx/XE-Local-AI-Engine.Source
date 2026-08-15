namespace XE_Local_AI_Engine.Tests.E2ETests.Tests;

using Microsoft.Playwright;
using XE_Local_AI_Engine.Tests.E2ETests.Common;

/// <summary>
///     Per-page interaction E2E tests for the node-settings page.
///     The real backend answers GET /api/local/v1/node-settings 200 in-process, so the
///     form populates from actual data stored in the per-session temp SQLite.
///     Save writes back to the same SQLite — harmless and verifiable via the success alert.
/// </summary>
[Category("Page")]
public sealed class NodeSettingsPageE2ETests : XESerialE2ETestBase
{
    private const string TestWorkspacePrefix = "xe-e2e-mcp-workspace-";

    [Test]
    public async Task NodeSettings_Page_Renders_Heading_And_Card()
    {
        await Page.GotoAsync($"{NodeAppUrl}/node-settings", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });

        // Main page heading rendered unconditionally.
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions
            {
                Name = "Node settings"
            }))
            .ToBeVisibleAsync();

        // Card heading for the runtime settings card.
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions
            {
                Name = "Local chat runtime"
            }))
            .ToBeVisibleAsync();
    }

    [Test]
    public async Task NodeSettings_Page_Timeout_Input_Populates_From_Backend()
    {
        await Page.GotoAsync($"{NodeAppUrl}/node-settings", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });

        // Use GetByLabel to locate the NumberInput via its Mantine <label for=...> association.
        // GetByText on the label element is unreliable because Mantine wraps label text in a span
        // inside a <label>, which Playwright's text locator does not surface as a top-level match.
        var timeoutInput = Page.GetByLabel("Maximum message request timeout");
        await Expect(timeoutInput).ToBeVisibleAsync();
        await Expect(timeoutInput).ToBeEnabledAsync();

        // The input must carry a numeric value — backend populated the form.
        var inputValue = await timeoutInput.InputValueAsync();
        var trimmed = inputValue.Replace(" seconds", string.Empty, StringComparison.Ordinal).Trim();
        await Assert.That(int.TryParse(trimmed, out var parsed) && parsed > 0).IsTrue();
    }

    [Test]
    public async Task NodeSettings_Page_Save_Settings_Shows_Success_Notification()
    {
        await Page.GotoAsync($"{NodeAppUrl}/node-settings", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });

        // Wait for the form to be populated from the backend before interacting.
        var timeoutInput = Page.GetByLabel("Maximum message request timeout");
        await Expect(timeoutInput).ToBeEnabledAsync();

        // Change the value to a known-valid number (300 seconds is within any reasonable range).
        await timeoutInput.FillAsync("300");

        // Save settings — button must be enabled after a valid change.
        var saveButton = Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
        {
            Name = "Save settings"
        });
        await Expect(saveButton).ToBeEnabledAsync();
        await saveButton.ClickAsync();

        // The component sets a green alert on successful save.
        await Expect(Page.GetByText("Node settings saved.").First).ToBeVisibleAsync();
    }

    [Test]
    public async Task NodeSettings_McpWorkspace_Create_RendersOpaqueReadOnlyRow_Then_Revoke_RemovesIt()
    {
        var workspaceDirectory = Directory.CreateTempSubdirectory(TestWorkspacePrefix);
        var alias = $"e2e-workspace-{Guid.NewGuid():N}";
        var pageErrors = new List<string>();
        Page.PageError += (_, error) => pageErrors.Add(error);

        try
        {
            await Page.GotoAsync($"{NodeAppUrl}/node-settings", new PageGotoOptions
            {
                WaitUntil = WaitUntilState.NetworkIdle
            });

            await Expect(Page.GetByTestId("mcp-workspaces-card")).ToBeVisibleAsync();
            await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions
                {
                    Name = "MCP workspace access"
                }))
                .ToBeVisibleAsync();
            await Expect(Page.GetByText("Could not load workspace access.")).ToHaveCountAsync(0);

            var aliasInput = Page.GetByTestId("mcp-workspace-alias");
            var pathInput = Page.GetByTestId("mcp-workspace-path");
            await aliasInput.FillAsync(alias);
            await pathInput.FillAsync(workspaceDirectory.FullName);

            var createResponse = await Page.RunAndWaitForResponseAsync(async () =>
                {
                    await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
                    {
                        Name = "Add read-only workspace"
                    }).ClickAsync();
                    await Expect(pathInput).ToHaveValueAsync("");
                },
                response => response.Url.EndsWith("/api/local/v1/workspaces", StringComparison.OrdinalIgnoreCase)
                            && string.Equals(response.Request.Method, "POST", StringComparison.OrdinalIgnoreCase),
                new PageRunAndWaitForResponseOptions
                {
                    Timeout = 10_000
                });

            await Assert.That(createResponse.Status).IsEqualTo(200);

            var aliasCell = Page.GetByRole(AriaRole.Cell, new PageGetByRoleOptions
            {
                Name = alias,
                Exact = true
            });
            await Expect(aliasCell).ToBeVisibleAsync();
            var row = aliasCell.Locator("..");
            var cells = await row.GetByRole(AriaRole.Cell).AllInnerTextsAsync();
            await Assert.That(cells.Count).IsEqualTo(4);
            await Assert.That(cells[0]).IsEqualTo(alias);
            await Assert.That(Guid.TryParse(cells[1], out _)).IsTrue();
            await Assert.That(string.Equals(cells[2], "Read only", StringComparison.OrdinalIgnoreCase)).IsTrue();
            await Assert.That(string.Join(' ', cells).Contains(workspaceDirectory.FullName, StringComparison.Ordinal)).IsFalse();

            var deleteResponse = await Page.RunAndWaitForResponseAsync(async () =>
                {
                    await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
                    {
                        Name = $"Revoke access to {alias}",
                        Exact = true
                    }).ClickAsync();
                    await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
                    {
                        Name = "Revoke",
                        Exact = true
                    }).ClickAsync();
                },
                response => response.Url.Contains("/api/local/v1/workspaces/", StringComparison.OrdinalIgnoreCase)
                            && string.Equals(response.Request.Method, "DELETE", StringComparison.OrdinalIgnoreCase),
                new PageRunAndWaitForResponseOptions
                {
                    Timeout = 10_000
                });

            await Assert.That(deleteResponse.Status).IsEqualTo(204);
            await Expect(aliasCell).ToHaveCountAsync(0, new LocatorAssertionsToHaveCountOptions
            {
                Timeout = 5000
            });
            await Assert.That(pageErrors.Count).IsEqualTo(0);
        }
        finally
        {
            TryDeleteTestWorkspaceDirectory(workspaceDirectory);
        }
    }

    private static void TryDeleteTestWorkspaceDirectory(DirectoryInfo workspaceDirectory)
    {
        try
        {
            var tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            var ownedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceDirectory.FullName));
            var isOwnedTempDirectory = string.Equals(Path.GetDirectoryName(ownedPath), tempRoot, StringComparison.Ordinal)
                                       && Path.GetFileName(ownedPath).StartsWith(TestWorkspacePrefix, StringComparison.Ordinal);
            workspaceDirectory.Refresh();
            if (isOwnedTempDirectory && workspaceDirectory.Exists)
            {
                workspaceDirectory.Delete(recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort E2E cleanup; the assertion result remains authoritative.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort E2E cleanup; the assertion result remains authoritative.
        }
    }
}
