namespace XE_Local_AI_Engine.Tests.E2ETests.Tests;

using System.Text.RegularExpressions;
using Microsoft.Playwright;
using XE_Local_AI_Engine.Tests.E2ETests.Common;

/// <summary>
///     Per-page interaction E2E tests for the Runtime Manager page (<c>/app/manager</c>).
///     <para>
///         PROBE strategy (task #4): Before asserting tab content we issue a raw HTTP call to
///         <c>GET /api/local/v1/runtime/status</c> (same token-extraction approach as
///         <see cref="TokenInjectionSpikeE2ETests" />) and inspect whether the response contains a
///         usable snapshot. In an unpaired, HostAgent-free test environment the endpoint may return
///         an error or an empty/null snapshot — in that case we assert only the static shell and tab
///         list and skip deeper content (commented below). If the probe returns a valid 200 snapshot
///         we also assert Status-tab content and tab-switching behaviour.
///     </para>
///     <para>
///         Branch taken at runtime: the probe result drives a runtime branch, but the compiled tests
///         are always the same; the branch comment documents what each path covers.
///     </para>
/// </summary>
[Category("Page")]
public sealed partial class RuntimeManagerPageE2ETests : XEE2ETestBase
{
    /// <summary>Matches: globalThis.__XE_LOCAL_OPERATOR_TOKEN__ = "&lt;hex-token&gt;";</summary>
    [GeneratedRegex(@"__XE_LOCAL_OPERATOR_TOKEN__\s*=\s*""(?<token>[0-9a-f]{64})""")]
    private static partial Regex InjectedTokenRegex();

    /// <summary>
    ///     Extracts the injected operator token from the /app HTML response.
    ///     Mirrors the approach used in TokenInjectionSpikeE2ETests.
    /// </summary>
    private static string? ExtractInjectedToken(string html)
    {
        var match = InjectedTokenRegex().Match(html);
        return match.Success ? match.Groups["token"].Value : null;
    }

    /// <summary>
    ///     Probes GET /api/local/v1/runtime/status with the operator token and Origin header.
    ///     Returns (statusCode, responseBody). A 200 with a non-empty body indicates a usable snapshot.
    /// </summary>
    private async Task<(int StatusCode, string Body)> ProbeRuntimeStatusAsync()
    {
        var serverAddress = Factory.ServerAddress.TrimEnd('/');
        using var client = new HttpClient();

        // Step 1: extract the operator token from the /app route (same as spike).
        var appResponse = await client.GetAsync($"{serverAddress}/app");
        var appBody = await appResponse.Content.ReadAsStringAsync();
        var token = ExtractInjectedToken(appBody);

        if (token is null)
        {
            return ((int)appResponse.StatusCode, string.Empty);
        }

        // Step 2: call the runtime/status API with the extracted token + same-origin Origin.
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{serverAddress}/api/local/v1/runtime/status");
        request.Headers.Add("X-Local-Operator", token);
        request.Headers.Add("Origin", serverAddress);

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        return ((int)response.StatusCode, body);
    }

    [Test]
    [Category("Page")]
    public async Task RuntimeManager_Page_Renders_Static_Shell_And_Tabs()
    {
        // Probe the runtime/status API before navigating so we know what content to expect.
        var (probeStatus, probeBody) = await ProbeRuntimeStatusAsync();

        // The probe result determines which assertions branch we take.
        // Status code 200 with a non-trivial body suggests a HostAgent snapshot is available.
        // In an unpaired test env without HostAgent the endpoint typically returns 4xx/5xx or
        // an empty/null snapshot — so the tab panels will not render.
        var hasSnapshot = probeStatus == 200
                          && probeBody.Contains("\"status\"", StringComparison.OrdinalIgnoreCase)
                          && probeBody.Contains("\"state\"", StringComparison.OrdinalIgnoreCase);

        await Page.GotoAsync($"{NodeAppUrl}/manager", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });

        // --- Unconditional static shell (always rendered) ---

        // Page heading is rendered unconditionally before snapshot data arrives.
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions
            {
                Name = "Runtime manager"
            }))
            .ToBeVisibleAsync();

        // Subtitle is always present.
        await Expect(Page.GetByText("Inspect HostAgent status").First).ToBeVisibleAsync();

        // The tab list renders only when a snapshot is present (gated by {snapshot ? …}).
        // We assert the tab list here when a snapshot is available, or skip with a clear comment.
        if (hasSnapshot)
        {
            // FULL BRANCH: snapshot available — assert tab list and tab-switching.
            // The Tabs component renders when getRuntimeManagerStatus returns a valid snapshot.
            var statusTab = Page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions
            {
                Name = "Status"
            });
            var componentsTab = Page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions
            {
                Name = "Components"
            });
            var manifestTab = Page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions
            {
                Name = "Manifest"
            });
            var logsTab = Page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions
            {
                Name = "Logs"
            });

            await Expect(statusTab).ToBeVisibleAsync();
            await Expect(componentsTab).ToBeVisibleAsync();
            await Expect(manifestTab).ToBeVisibleAsync();
            await Expect(logsTab).ToBeVisibleAsync();

            // Status tab is selected by default — "Substrate status" card title visible.
            await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions
                {
                    Name = "Substrate status"
                }))
                .ToBeVisibleAsync();

            // Click Components tab and verify the panel switches.
            await componentsTab.ClickAsync();
            await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions
                {
                    Name = "Runtime components"
                }))
                .ToBeVisibleAsync();

            // Click Manifest tab and verify the panel switches.
            await manifestTab.ClickAsync();
            // Manifest panel heading is "Runtime manifest".
            await Expect(Page.GetByText("Runtime manifest").First).ToBeVisibleAsync();

            // NOTE: We deliberately do NOT click the "Follow logs" tab button to start a
            // SignalR stream — per task constraint.
        }
        else
        {
            // STATIC-ONLY BRANCH: no usable snapshot from HostAgent (unpaired, no hosted services).
            // The Tabs component is gated behind {snapshot ? …} in RuntimeManager.tsx and will
            // not render. We assert the loading/error indicator instead.
            //
            // Deeper tab content (Status, Components, Manifest, Logs panels) is DEFERRED
            // and requires a live HostAgent. See task #4 comment in the test plan.
            //
            // Wait for either a loader or an error alert to confirm the page reacted to the
            // failed/missing status response.
            var loader = Page.GetByRole(AriaRole.Status).First;
            var errorAlert = Page.GetByRole(AriaRole.Alert).First;

            // At least one of loader or error must be visible to confirm the page settled.
            // Playwright auto-waits on ToBeVisibleAsync; we try loader first with a short
            // timeout fallback, then accept an error alert.
            try
            {
                await Expect(loader).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
                {
                    Timeout = 3000
                });
            }
            catch (PlaywrightException)
            {
                // Loader gone (loading finished without snapshot) — error alert should be visible.
                await Expect(errorAlert).ToBeVisibleAsync();
            }
        }
    }
}
