namespace XE_Local_AI_Engine.Tests.E2ETests.Tests;

using Microsoft.Playwright;
using XE_Local_AI_Engine.Tests.E2ETests.Common;

/// <summary>
///     E2E tests for Runtime Manager container action controls and the log-stream / follow-logs UI
///     (plan Phases 6.3 / 6.4).
///     <para>
///         PROBE strategy (mirrors <see cref="RuntimeManagerPageE2ETests" />): before asserting
///         interactive surface we issue a raw HTTP GET to <c>/api/local/v1/runtime/status</c> and
///         check whether the response contains a usable HostAgent snapshot. In an unpaired,
///         HostAgent-free test environment (the normal CI case) the endpoint returns an error or
///         null snapshot — the tab panels do not render and the tests assert the static degraded state
///         instead. When a snapshot is present the tests assert the full interactive surface.
///     </para>
///     <para>
///         No real Docker or HostAgent is required. The action POST and SignalR negotiate requests
///         target the in-process FastEndpoints host; the tests assert the request fires rather than a
///         successful round-trip — same pattern as
///         <see cref="NodeBindingPageE2ETests.NodeBinding_StartBinding_Click_Produces_Deterministic_State_Change" />.
///     </para>
/// </summary>
[Category("Page")]
public sealed class RuntimeManagerContainerActionE2ETests : XEE2ETestBase
{
    /// <summary>
    ///     Probes GET /api/local/v1/runtime/status without browser auth bootstrap.
    ///     Returns (statusCode, responseBody). A 200 body containing "status" and "state"
    ///     fields indicates a usable HostAgent snapshot.
    /// </summary>
    private async Task<(int StatusCode, string Body)> ProbeRuntimeStatusAsync()
    {
        var serverAddress = Factory.ServerAddress.TrimEnd('/');
        using var client = new HttpClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{serverAddress}/api/local/v1/runtime/status");
        request.Headers.Add("Origin", serverAddress);

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        return ((int)response.StatusCode, body);
    }

    private static bool IsUsableSnapshot(int statusCode, string body)
    {
        return statusCode == 200
               && body.Contains("\"status\"", StringComparison.OrdinalIgnoreCase)
               && body.Contains("\"state\"", StringComparison.OrdinalIgnoreCase);
    }

    private async Task NavigateToManagerAsync()
    {
        await Page.GotoAsync($"{NodeAppUrl}/manager", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });
    }

    private async Task AssertUnpairedDegradedStateAsync()
    {
        // Static heading always renders; tabs do not.
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions
            {
                Name = "Runtime manager"
            }))
            .ToBeVisibleAsync();

        var loader = Page.GetByRole(AriaRole.Status).First;
        var errorAlert = Page.GetByRole(AriaRole.Alert).First;

        try
        {
            await Expect(loader).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
            {
                Timeout = 3000
            });
        }
        catch (PlaywrightException)
        {
            // Loading finished without snapshot — error alert should be visible.
            await Expect(errorAlert).ToBeVisibleAsync();
        }
    }

    // -----------------------------------------------------------------------------------------
    // Container action controls
    // -----------------------------------------------------------------------------------------

    /// <summary>
    ///     Asserts the Components tab panel renders the "Runtime components" heading, a container
    ///     action table, and Start / Stop / Restart buttons for each row when a snapshot is
    ///     available. In the unpaired state the tab is absent and the page settles to the
    ///     loading / error degraded state.
    /// </summary>
    [Test]
    [Category("Page")]
    public async Task RuntimeManager_Components_Tab_Renders_Action_Buttons()
    {
        var (statusCode, body) = await ProbeRuntimeStatusAsync();
        await NavigateToManagerAsync();

        if (!IsUsableSnapshot(statusCode, body))
        {
            await AssertUnpairedDegradedStateAsync();
            return;
        }

        // FULL BRANCH: snapshot present — switch to Components tab.
        var componentsTab = Page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions
        {
            Name = "Components"
        });
        await Expect(componentsTab).ToBeVisibleAsync();
        await componentsTab.ClickAsync();

        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions
            {
                Name = "Runtime components"
            }))
            .ToBeVisibleAsync();

        if (await Page.GetByText("No runtime containers reported.").IsVisibleAsync())
        {
            // No containers in snapshot — action buttons are not rendered. Assert the
            // empty-state text as the positive existence signal for the panel.
            return;
        }

        // At least one row: all three action buttons must be present in the DOM.
        // The buttons are disabled={actionMutation.isPending} only — they are enabled
        // when no mutation is in flight, regardless of container state.
        await Expect(Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
            {
                Name = "Start"
            }).First)
            .ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
            {
                Name = "Stop"
            }).First)
            .ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
            {
                Name = "Restart"
            }).First)
            .ToBeVisibleAsync();
    }

    /// <summary>
    ///     Asserts that clicking a container action button fires a POST to
    ///     <c>/api/local/v1/runtime/containers/action</c>. Request-fires is the deterministic
    ///     signal; response success is not checked because no live HostAgent is present.
    ///     Skipped with a documented assertion when no snapshot or no containers are available.
    /// </summary>
    [Test]
    [Category("Page")]
    public async Task RuntimeManager_ContainerAction_Stop_Click_Issues_POST()
    {
        var (statusCode, body) = await ProbeRuntimeStatusAsync();
        await NavigateToManagerAsync();

        if (!IsUsableSnapshot(statusCode, body))
        {
            await AssertUnpairedDegradedStateAsync();
            return;
        }

        var componentsTab = Page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions
        {
            Name = "Components"
        });
        await componentsTab.ClickAsync();
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions
            {
                Name = "Runtime components"
            }))
            .ToBeVisibleAsync();

        if (await Page.GetByText("No runtime containers reported.").IsVisibleAsync())
        {
            // No containers — action buttons absent; nothing to click. Assert panel rendered.
            return;
        }

        // Stop is a consistent choice: all three buttons share identical enabled/disabled logic
        // (disabled only while actionMutation.isPending), so any enabled button works.
        var stopButton = Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
        {
            Name = "Stop"
        }).First;
        await Expect(stopButton).ToBeVisibleAsync();
        await Expect(stopButton).ToBeEnabledAsync();

        await Page.RunAndWaitForRequestAsync(async () => await stopButton.ClickAsync(),
            request => request.Url.Contains("runtime/containers/action", StringComparison.OrdinalIgnoreCase)
                       && string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase));
    }

    // -----------------------------------------------------------------------------------------
    // Log-stream / Follow-logs UI
    // -----------------------------------------------------------------------------------------

    /// <summary>
    ///     Asserts the Logs tab panel renders the "Runtime logs" heading, a Container Select,
    ///     and the "Follow logs" button when a snapshot is available. In the unpaired state the
    ///     tab is absent.
    /// </summary>
    [Test]
    [Category("Page")]
    public async Task RuntimeManager_Logs_Tab_Renders_FollowLogs_Button()
    {
        var (statusCode, body) = await ProbeRuntimeStatusAsync();
        await NavigateToManagerAsync();

        if (!IsUsableSnapshot(statusCode, body))
        {
            await AssertUnpairedDegradedStateAsync();
            return;
        }

        var logsTab = Page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions
        {
            Name = "Logs"
        });
        await Expect(logsTab).ToBeVisibleAsync();
        await logsTab.ClickAsync();

        // Panel heading confirms the switch.
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions
            {
                Name = "Runtime logs"
            }))
            .ToBeVisibleAsync();

        // Container selector (label "Container") must be present.
        await Expect(Page.GetByLabel("Container")).ToBeVisibleAsync();

        if (await Page.GetByText("No runtime containers are available for logs.").IsVisibleAsync())
        {
            // No containers in snapshot — Follow logs button is disabled (no container selected).
            // Assert it is at least visible to confirm the panel rendered correctly.
            await Expect(Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
                {
                    Name = "Follow logs"
                }))
                .ToBeVisibleAsync();
            return;
        }

        // Containers present — the first container is auto-selected (see RuntimeManager.tsx
        // useEffect) so Follow logs must be enabled.
        await Expect(Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
            {
                Name = "Follow logs"
            }))
            .ToBeEnabledAsync();
    }

    /// <summary>
    ///     Asserts that clicking "Follow logs" fires a SignalR negotiate request to the runtime
    ///     hub (<c>/api/local/v1/runtime/hub/negotiate</c>). The negotiate is a POST issued by
    ///     the SignalR client when <c>startLogFollow</c> builds the hub connection.
    ///     Skipped with a documented assertion when no snapshot or no containers are available.
    /// </summary>
    [Test]
    [Category("Page")]
    public async Task RuntimeManager_FollowLogs_Click_Issues_SignalR_Negotiate()
    {
        var (statusCode, body) = await ProbeRuntimeStatusAsync();
        await NavigateToManagerAsync();

        if (!IsUsableSnapshot(statusCode, body))
        {
            await AssertUnpairedDegradedStateAsync();
            return;
        }

        var logsTab = Page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions
        {
            Name = "Logs"
        });
        await logsTab.ClickAsync();
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions
            {
                Name = "Runtime logs"
            }))
            .ToBeVisibleAsync();

        var followButton = Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
        {
            Name = "Follow logs"
        });
        await Expect(followButton).ToBeVisibleAsync();

        if (!await followButton.IsEnabledAsync())
        {
            // No container selected (empty snapshot) — Follow logs disabled; not clickable.
            // Assert the disabled state as a positive UI-correctness signal.
            await Expect(followButton).ToBeDisabledAsync();
            return;
        }

        // Clicking Follow logs starts the SignalR hub connection. The client posts to the
        // negotiate endpoint before opening the streaming connection.
        await Page.RunAndWaitForRequestAsync(async () => await followButton.ClickAsync(),
            request => request.Url.Contains("runtime/hub", StringComparison.OrdinalIgnoreCase),
            new PageRunAndWaitForRequestOptions
            {
                Timeout = 5000
            });
    }
}
