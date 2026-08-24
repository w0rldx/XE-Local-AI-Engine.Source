namespace XE_Local_AI_Engine.Tests.E2ETests.Tests;

using Microsoft.Playwright;
using XE_Local_AI_Engine.Testing.FakeOllama;
using XE_Local_AI_Engine.Tests.E2ETests.Common;

/// <summary>
///     Browser-driven E2E for the work-session happy path: create a session from the list page, land on the detail
///     route, start it, and watch the supervisor's step loop drive the three panes to a finished session.
///     <para>
///         Flow:
///         <list type="number">
///             <item>Go to <c>/work-sessions</c> and create a session through the dialog, picking the seeded General persona.</item>
///             <item>Land on <c>/work-sessions/{id}</c>: three panes, a <c>Draft</c> badge, Start enabled.</item>
///             <item>Press Start. The supervisor picks the session up out of band (the endpoint answers 202).</item>
///             <item>
///                 <c>FakeOllamaState.ToolCallScript</c> drives the loop deterministically: the first step calls
///                 <c>update_work_plan</c> to add one task, the next calls <c>complete_work_session</c>.
///             </item>
///             <item>The task appears in the left pane and the status badge reaches <c>Completed</c>.</item>
///         </list>
///     </para>
///     <para>
///         Serial (<see cref="XESerialE2ETestBase" />) because it mutates <c>FakeOllamaState</c> on the shared host, and
///         because a work session holds the node's single invocation slot for the length of its run.
///     </para>
///     <para>
///         The seeded personas are the only agents that can run a session — the four state tools are held out of the
///         general chat offer, so the agent-send intersection drops them for an agent built through the UI. The E2E host
///         therefore re-adds <c>WorkSessionAgentSeeder</c> after its blanket <c>RemoveAll&lt;IHostedService&gt;</c>; see
///         the comment beside that registration.
///     </para>
/// </summary>
[Category("Page")]
public sealed class WorkSessionsPageE2ETests : XESerialE2ETestBase
{
    private const string GeneralAgentName = "Work Session — General";
    private const string PlannedTaskTitle = "Read the two candidate specs";

    [After(Test)]
    public void ResetScripts()
    {
        Factory.FakeOllamaState.ChatScript = null;
        Factory.FakeOllamaState.ToolCallScript = null;
    }

    /// <summary>
    ///     Drives the step loop: the first assistant turn adds a task, every later one finishes the session.
    ///     <para>
    ///         Keyed on the presence of a <c>tool</c>-role message rather than on a call counter, because the FICC loop
    ///         calls the provider again inside the same step after a tool result and the supervisor starts a fresh step
    ///         after that — a counter would desynchronise on the first retry and re-add the task forever. Only the two
    ///         scripted tools run here, so "a tool result exists" means exactly "the plan has been written".
    ///     </para>
    /// </summary>
    private static FakeOllamaToolCall? ScriptStep(IReadOnlyList<OllamaSharp.Models.Chat.Message> messages)
    {
        var planned = messages.Any(message =>
            string.Equals(message.Role?.ToString(), "tool", StringComparison.OrdinalIgnoreCase));

        if (!planned)
        {
            return new FakeOllamaToolCall
            {
                Name = "update_work_plan",
                Arguments = new
                {
                    operations = new[]
                    {
                        new
                        {
                            op = "add",
                            title = PlannedTaskTitle,
                            status = "Active"
                        }
                    }
                }
            };
        }

        return new FakeOllamaToolCall
        {
            Name = "complete_work_session",
            Arguments = new
            {
                summary = "Both specs read; the objective is met."
            }
        };
    }

    [Test]
    [Category("Page")]
    public async Task Work_Session_Runs_From_Create_To_Completed()
    {
        Factory.FakeOllamaState.ToolCallScript = ScriptStep;

        await Page.GotoAsync($"{NodeAppUrl}/work-sessions", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });

        await Expect(Page.GetByTestId("work-sessions-page")).ToBeVisibleAsync();
        await Page.GetByTestId("work-sessions-create").ClickAsync();
        await Expect(Page.GetByTestId("create-work-session-dialog")).ToBeVisibleAsync();

        await Page.GetByTestId("create-work-session-title").FillAsync("E2E work session");
        await Page.GetByTestId("create-work-session-objective")
                  .FillAsync("Decide between the two candidate specs and say which one wins.");

        // The seeded General persona is picked through the SAME AgentSelectorCard the chat composer uses.
        await Page.GetByTestId("chat-agent-selector-trigger").ClickAsync();
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
        {
            Name = GeneralAgentName
        }).First.ClickAsync();

        var submit = Page.GetByTestId("create-work-session-submit");
        await Expect(submit).ToBeEnabledAsync();
        await Page.RunAndWaitForResponseAsync(async () => await submit.ClickAsync(),
            response => response.Url.Contains("/api/local/v1/work-sessions", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(response.Request.Method, "POST", StringComparison.OrdinalIgnoreCase),
            new PageRunAndWaitForResponseOptions
            {
                Timeout = 10_000
            });

        // The detail route: three panes side by side, a Draft session, Start offered.
        await Expect(Page.GetByTestId("work-session-detail-grid")).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
        {
            Timeout = 10_000
        });
        await Expect(Page.GetByTestId("work-session-plan-panel")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("work-session-conversation-pane")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("work-session-side-panel")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("work-session-status-badge")).ToHaveTextAsync("Draft");

        var start = Page.GetByTestId("work-session-start");
        await Expect(start).ToBeEnabledAsync();
        await start.ClickAsync();

        // The plan the agent wrote itself, live in the left pane. The hub pushes `task`; the poll fallback covers a
        // hub that could not connect, so this assertion holds either way — it just takes up to 3s longer.
        await Expect(Page.GetByTestId("work-session-task-tree")).ToContainTextAsync(PlannedTaskTitle,
            new LocatorAssertionsToContainTextOptions
            {
                Timeout = 60_000
            });

        // complete_work_session lands the terminal status.
        await Expect(Page.GetByTestId("work-session-status-badge")).ToHaveTextAsync("Completed",
            new LocatorAssertionsToHaveTextOptions
            {
                Timeout = 60_000
            });

        // A finished session takes no further controls.
        await Expect(Page.GetByTestId("work-session-start")).ToHaveCountAsync(0);
        await Expect(Page.GetByTestId("work-session-pause")).ToHaveCountAsync(0);
        await Expect(Page.GetByTestId("work-session-cancel")).ToHaveCountAsync(0);
    }
}
