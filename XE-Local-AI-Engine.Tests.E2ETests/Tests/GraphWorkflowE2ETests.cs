namespace XE_Local_AI_Engine.Tests.E2ETests.Tests;

using System.Text.RegularExpressions;
using Microsoft.Playwright;
using XE_Local_AI_Engine.Tests.E2ETests.Common;

/// <summary>
///     Browser-driven E2E for the one loop the graph-workflow surface exists for: author a graph in the editor, save
///     it, run it, answer the human gate it stops on, and watch the run finish.
///     <para>
///         Flow:
///         <list type="number">
///             <item>Create a definition on <c>/graph-workflows</c>, which opens the editor on a bare Start → End graph.</item>
///             <item>Drop an Agent and a Pause on the canvas, wire <c>start → agent-1 → pause-1 → end</c>, configure all three.</item>
///             <item>Save. The editor validates before it sends, so a graph that saves is a graph the server accepted.</item>
///             <item>Start a run with a JSON input. The endpoint answers out of band and the page switches to the run view.</item>
///             <item>The Agent node runs against FakeOllama and the Pause node parks the run on a decision.</item>
///             <item>Approve. The run reaches <c>Completed</c> and the End node carries the run's result document.</item>
///         </list>
///     </para>
///     <para>
///         Serial (<see cref="XESerialE2ETestBase" />) because the Agent node holds the node-wide invocation slot for the
///         length of its turn, exactly as a work session does: a pooled sibling sending a chat message would queue
///         behind it, and both would then fail on their own timeouts rather than on anything either one covers.
///     </para>
///     <para>
///         Three things the E2E host does for this test rather than the test doing them for itself, all in
///         <c>XENodeE2EWebApplicationFactory</c>: <c>GraphWorkflows:Enabled</c> is stated rather than inherited, the
///         dispatcher and its startup reconciler go back after the blanket <c>RemoveAll&lt;IHostedService&gt;</c> (a run
///         is advanced by that loop and by nothing else), and the capacity gate is replaced because this host has no
///         installed GGUF to size a model against. See the comments beside each one.
///     </para>
///     <para>
///         The first-run welcome modal is NOT dismissed here: the fixture seeds the admin with a terminal tour outcome,
///         so this suite signs in as a returning user and no modal opens over the page.
///     </para>
/// </summary>
[Category("GraphWorkflows")]
public sealed class GraphWorkflowE2ETests : XESerialE2ETestBase
{
    /// <summary>The FakeOllama chat model the Agent node pins. Pinned on the NODE, so no agent definition is involved.</summary>
    private const string FakeChatModel = "qwen3.5:0.8b";

    /// <summary>Palette keys are minted per kind over one namespace, so the first Agent and Pause are always these.</summary>
    private const string AgentKey = "agent-1";

    private const string PauseKey = "pause-1";

    /// <summary>
    ///     Wider than the harness default so the editor renders its three-column layout (list, canvas, config) rather
    ///     than moving the config panel into a drawer, and so every card is on screen for the handle drags below.
    /// </summary>
    public override BrowserNewContextOptions ContextOptions(TestContext testContext)
    {
        var options = base.ContextOptions(testContext);
        options.ViewportSize = new ViewportSize
        {
            Width = 1600,
            Height = 1000
        };
        return options;
    }

    [Test]
    [Category("GraphWorkflows")]
    public async Task Graph_Workflow_Runs_Through_Its_Pause_And_Completes_On_Approval()
    {
        var workflowName = $"E2E approval loop {Guid.NewGuid():N}";

        await Page.GotoAsync($"{NodeAppUrl}/graph-workflows", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });
        await Expect(Page.GetByTestId("graph-workflows-page")).ToBeVisibleAsync();

        // 1. A new definition. The create path opens the editor on the starter graph: Start and End, joined by one edge.
        await Page.GetByTestId("gw-definition-create").ClickAsync();
        await Page.GetByTestId("gw-definition-meta-name").FillAsync(workflowName);
        await Page.GetByTestId("gw-definition-meta-submit").ClickAsync();

        await Expect(Page.GetByTestId("graph-workflow-editor")).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
        {
            Timeout = 15_000
        });
        await Expect(Node("start")).ToBeVisibleAsync();
        await Expect(Node("end")).ToBeVisibleAsync();
        // The create dialog's overlay covers the canvas while it fades, and a raw mouse click at a coordinate is not
        // actionability-checked — it would be swallowed there rather than reaching the edge below.
        await Expect(Page.GetByTestId("gw-definition-meta-name")).ToHaveCountAsync(0);
        await Expect(Page.Locator(".mantine-Modal-overlay")).ToHaveCountAsync(0);
        await WaitForCanvasSettledAsync();

        // 2. The starter edge has to go: Start's only out-edge must reach the Agent, not the End. React Flow owns the
        //    edge markup and puts no test id on it, so the edge is reached by geometry — but the DRAWER it opens is
        //    product markup, and waiting for that is what proves the click landed on the edge rather than the pane.
        await RemoveStarterEdgeAsync();

        // 3. The two nodes this loop is about. Auto-arrange first: a palette node lands 60px below the last one, which
        //    is less than a card is tall, so the cards overlap until something lays them out.
        await Page.GetByTestId("graph-workflow-palette-agent").ClickAsync();
        await Page.GetByTestId("graph-workflow-palette-pause").ClickAsync();
        await Expect(Node(AgentKey)).ToBeVisibleAsync();
        await Expect(Node(PauseKey)).ToBeVisibleAsync();
        await Page.GetByTestId("graph-workflow-auto-arrange").ClickAsync();
        await FitViewAsync();

        // 4. Configure BEFORE wiring: dropping Reject leaves the Pause one source handle, so the wiring below cannot
        //    pick the wrong one, and the graph stops carrying the unroutable decision the validator refuses.
        await SelectNodeAsync(PauseKey);
        await Page.GetByTestId("gw-node-config-prompt").FillAsync("Does this answer look right?");
        await Page.GetByTestId("gw-node-config-decision-Reject").UncheckAsync();

        await SelectNodeAsync(AgentKey);
        await Page.GetByTestId("gw-node-config-instructions")
                  .FillAsync("You are an E2E probe. Answer the question in one short sentence.");
        // The node pins its own model, which is what makes this an unattended local run with no agent definition
        // behind it. The list is the node's installed chat models, which here is what FakeOllama serves.
        await Page.GetByTestId("gw-node-config-model").ClickAsync();
        await Page.GetByRole(AriaRole.Option, new PageGetByRoleOptions
        {
            Name = FakeChatModel
        }).First.ClickAsync();
        await Expect(Page.GetByTestId("gw-node-config-model")).ToHaveValueAsync(new Regex(Regex.Escape(FakeChatModel)));

        // 5. Wire it. Fit the view between drags: the cards move as the layout gains ranks, and a handle that is off
        //    screen has no bounding box to aim at.
        await FitViewAsync();
        await ConnectAsync(SourceHandle("start"), TargetHandle(AgentKey));
        await FitViewAsync();
        await ConnectAsync(SourceHandle(AgentKey), TargetHandle(PauseKey));
        await FitViewAsync();
        await ConnectAsync(SourceHandle(PauseKey, "Approve"), TargetHandle("end"));
        await Page.GetByTestId("graph-workflow-auto-arrange").ClickAsync();
        await FitViewAsync();

        // 6. Save. The strip is the editor's own verdict, so an empty strip and an enabled Save are the same statement
        //    made twice — and the strip is the half that names the rule when a drag missed its handle.
        var save = Page.GetByTestId("gw-page-save");
        await Expect(save).ToBeEnabledAsync(new LocatorAssertionsToBeEnabledOptions
        {
            Timeout = 15_000
        });
        await Expect(Page.GetByTestId("graph-workflow-validation-strip")).ToHaveCountAsync(0);

        await save.ClickAsync();
        // Save going disabled IS the saved-and-clean signal: the button is bound to the canvas being dirty.
        await Expect(save).ToBeDisabledAsync(new LocatorAssertionsToBeDisabledOptions
        {
            Timeout = 20_000
        });

        // 7. Start a run on the saved version, with an input the Start node hands down the graph.
        await Page.GetByTestId("gw-page-start-run").ClickAsync();
        await Expect(Page.GetByTestId("graph-workflow-start-run-dialog")).ToBeVisibleAsync();
        await SetCodeEditorAsync("graph-workflow-start-run-input", """{"topic":"the approval loop"}""");
        await Expect(Page.GetByTestId("graph-workflow-start-run-invalid-json")).ToHaveCountAsync(0);

        var submit = Page.GetByTestId("graph-workflow-start-run-submit");
        await Expect(submit).ToBeEnabledAsync();
        await submit.ClickAsync();

        // The run id lands in the URL, which is what makes a run view reload-stable and is how the run is addressed below.
        await Page.WaitForURLAsync(new Regex("runId="), new PageWaitForURLOptions
        {
            Timeout = 30_000
        });
        await Expect(Page.GetByTestId("graph-workflow-run-toolbar")).ToBeVisibleAsync();

        var runId = Regex.Match(Page.Url, "runId=([^&]+)").Groups[1].Value;
        await Assert.That(runId).IsNotEmpty();

        // 8. The Agent node runs off the dispatcher's loop against FakeOllama, and the Pause node parks the run on a
        //    person. Read the statuses off the node-run table rather than the canvas: the table is the same rows and
        //    needs no viewport geometry to be legible.
        await Expect(NodeStatus(AgentKey)).ToHaveTextAsync("Succeeded", new LocatorAssertionsToHaveTextOptions
        {
            Timeout = 120_000
        });
        await Expect(NodeStatus(PauseKey)).ToHaveTextAsync("Waiting for a decision", new LocatorAssertionsToHaveTextOptions
        {
            Timeout = 60_000
        });
        await Expect(RunStatus(runId)).ToHaveTextAsync("Waiting for a decision", new LocatorAssertionsToHaveTextOptions
        {
            Timeout = 30_000
        });

        // 9. Answer it. The panel offers exactly the decisions the node was configured with — one, here, which is what
        //    proves the Reject unchecked above reached the saved graph and not just the canvas.
        await Page.GetByTestId($"graph-workflow-node-select-{PauseKey}").ClickAsync();
        await Expect(Page.GetByTestId("graph-workflow-decision-panel")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("graph-workflow-decision-Reject")).ToHaveCountAsync(0);
        await Page.GetByTestId("graph-workflow-decision-Approve").ClickAsync();

        // The panel is replaced by the answered node's own output once the decision is recorded.
        await Expect(Page.GetByTestId("graph-workflow-node-panel-pause")).ToContainTextAsync("Approve",
            new LocatorAssertionsToContainTextOptions
            {
                Timeout = 30_000
            });
        await Page.GetByTestId("graph-workflow-node-panel-close").ClickAsync();

        // 10. The run carries on past the gate and finishes.
        await Expect(NodeStatus(PauseKey)).ToHaveTextAsync("Succeeded", new LocatorAssertionsToHaveTextOptions
        {
            Timeout = 60_000
        });
        await Expect(NodeStatus("end")).ToHaveTextAsync("Succeeded", new LocatorAssertionsToHaveTextOptions
        {
            Timeout = 60_000
        });
        await Expect(RunStatus(runId)).ToHaveTextAsync("Completed", new LocatorAssertionsToHaveTextOptions
        {
            Timeout = 60_000
        });

        // And the End node's output is the run's result document, whose `outcome` is what kind of end this was.
        await Page.GetByTestId("graph-workflow-node-select-end").ClickAsync();
        await Expect(Page.GetByTestId("graph-workflow-node-panel")).ToBeVisibleAsync();
        await Page.GetByTestId("graph-workflow-node-panel-tab-output").ClickAsync();
        await Expect(Page.GetByTestId("graph-workflow-node-panel-output-empty")).ToHaveCountAsync(0);
        await Expect(Page.GetByTestId("graph-workflow-node-panel-output")).ToContainTextAsync("completed",
            new LocatorAssertionsToContainTextOptions
            {
                Timeout = 15_000
            });
    }

    private ILocator Node(string key) =>
        Page.GetByTestId($"graph-workflow-node-{key}");

    private ILocator NodeStatus(string key) =>
        Page.GetByTestId($"graph-workflow-node-status-{key}");

    private ILocator RunStatus(string runId) =>
        Page.GetByTestId($"graph-workflow-run-status-{runId}");

    private ILocator TargetHandle(string key) =>
        Node(key).GetByTestId("graph-workflow-handle-target");

    /// <summary>A node's out-handle: the single default one, or the named handle a Pause grows per offered decision.</summary>
    private ILocator SourceHandle(string key, string? decision = null) =>
        Node(key).GetByTestId(decision is null ? "graph-workflow-handle-source-default" : $"graph-workflow-handle-source-{decision}");

    /// <summary>
    ///     Frames the whole graph. React Flow's own control, not a product one — there is no product affordance for it,
    ///     and the alternative is aiming drags at cards that are off screen.
    /// </summary>
    private async Task FitViewAsync()
    {
        await Page.Locator(".react-flow__controls-fitview").First.ClickAsync();
        await Expect(Node("end")).ToBeVisibleAsync();
        await WaitForCanvasSettledAsync();
    }

    /// <summary>
    ///     Waits until React Flow's viewport transform holds still across two animation frames.
    ///     <para>
    ///         Every geometry read below — a handle's bounding box, an edge's path midpoint — is a coordinate in that
    ///         transform, and React Flow re-frames the graph off an animation frame after it mounts and after every fit.
    ///         Reading before it settles yields a point that is correct for a viewport the browser has already replaced,
    ///         and the click or the drag then lands on the pane. This is a gate on the transform itself, not a wait on a
    ///         clock: it resolves on the first pair of frames that agree.
    ///     </para>
    /// </summary>
    private async Task WaitForCanvasSettledAsync() =>
        await Page.WaitForFunctionAsync("""
                                        () => new Promise((resolve) => {
                                            const read = () => document.querySelector('.react-flow__viewport')?.style?.transform ?? '';
                                            const before = read();
                                            if (before.length === 0) {
                                                resolve(false);
                                                return;
                                            }
                                            requestAnimationFrame(() => requestAnimationFrame(() => resolve(read() === before)));
                                        })
                                        """);

    /// <summary>
    ///     Selects a node the way an operator does — by clicking its card — and waits for the config panel that proves
    ///     the click selected rather than started a drag. Clicks near the card's corner: the handles sit on its edges.
    /// </summary>
    private async Task SelectNodeAsync(string key)
    {
        await Node(key).ClickAsync(new LocatorClickOptions
        {
            Position = new Position
            {
                X = 24,
                Y = 20
            }
        });
        await Expect(Page.GetByTestId("gw-node-config")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("gw-node-config-key")).ToHaveValueAsync(key);
    }

    /// <summary>
    ///     Deletes the starter <c>start → end</c> edge. The edge is selected at its path MIDPOINT: a bezier's
    ///     bounding-box centre is not on the path, so the obvious click misses it and lands on the pane.
    /// </summary>
    private async Task RemoveStarterEdgeAsync()
    {
        var edge = Page.Locator(".react-flow__edge").First;
        await Expect(edge).ToBeVisibleAsync();

        var point = await edge.EvaluateAsync<float[]>("""
                                                      (element) => {
                                                          const path = element.querySelector('path');
                                                          const middle = path.getPointAtLength(path.getTotalLength() / 2);
                                                          const screen = new DOMPoint(middle.x, middle.y).matrixTransform(path.getScreenCTM());
                                                          return [screen.x, screen.y];
                                                      }
                                                      """);

        await Page.Mouse.ClickAsync(point[0], point[1]);

        // The drawer is the product's own confirmation that the edge is selected, and its Remove button is the
        // affordance an operator uses — so the deletion never depends on where keyboard focus happened to be.
        var edgeConfig = Page.GetByTestId("gw-edge-config");
        try
        {
            await Expect(edgeConfig).ToBeVisibleAsync();
        }
        catch (PlaywrightException)
        {
            // A geometry-driven click fails as "the drawer never opened", which says nothing about WHY. Name what was
            // actually under the point instead, so the next reader does not have to reconstruct it from a trace.
            var hit = await Page.EvaluateAsync<string>("([x, y]) => document.elementFromPoint(x, y)?.className?.baseVal ?? document.elementFromPoint(x, y)?.className ?? 'nothing'",
                new[]
                {
                    point[0],
                    point[1]
                });
            Assert.Fail($"Clicking the starter edge at ({point[0]}, {point[1]}) did not select it — the point is over '{hit}'.");
        }

        await Page.GetByTestId("gw-edge-config-remove").ClickAsync();
        await Expect(edgeConfig).ToHaveCountAsync(0);
        await Expect(Page.Locator(".react-flow__edge")).ToHaveCountAsync(0);
    }

    /// <summary>
    ///     Types into the shared Monaco-backed <c>CodeEditor</c>. Its textarea is visually hidden, so focus has to go
    ///     through the rendered view lines; the select-all clears whatever the field was seeded with.
    /// </summary>
    private async Task SetCodeEditorAsync(string testId, string text)
    {
        var lines = Page.GetByTestId(testId).Locator(".monaco-editor .view-lines").First;
        await Expect(lines).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
        {
            Timeout = 30_000
        });

        await lines.ClickAsync();
        await Page.Keyboard.PressAsync("Control+A");
        await Page.Keyboard.PressAsync("Delete");
        await Page.Keyboard.InsertTextAsync(text);
        await Expect(lines).ToContainTextAsync("topic");
    }

    /// <summary>
    ///     Draws a connection between two handles. React Flow tracks the pointer while a connection is in flight, so
    ///     the move is walked rather than jumped: a single hop can leave the target outside its snap radius.
    /// </summary>
    private async Task ConnectAsync(ILocator source, ILocator target)
    {
        var from = await source.BoundingBoxAsync() ?? throw new InvalidOperationException("The source handle is not on screen.");
        var to = await target.BoundingBoxAsync() ?? throw new InvalidOperationException("The target handle is not on screen.");

        var startX = from.X + from.Width / 2;
        var startY = from.Y + from.Height / 2;
        var endX = to.X + to.Width / 2;
        var endY = to.Y + to.Height / 2;

        await Page.Mouse.MoveAsync(startX, startY);
        await Page.Mouse.DownAsync();
        for (var step = 1; step <= 10; step++)
        {
            await Page.Mouse.MoveAsync(startX + ((endX - startX) * step / 10), startY + ((endY - startY) * step / 10));
        }

        await Page.Mouse.MoveAsync(endX, endY);
        await Page.Mouse.UpAsync();
    }
}
