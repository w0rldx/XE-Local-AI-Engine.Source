namespace XE_Local_AI_Engine.Tests.E2ETests.Tests;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Providers.Ollama.Implementation;
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
///             <item>The badge leaves <c>Draft</c> and the Events tab shows the supervisor's own lifecycle feed.</item>
///         </list>
///     </para>
///     <para>
///         Serial (<see cref="XESerialE2ETestBase" />) because a work session holds the node's single invocation slot
///         for the length of its run, and because it writes shared node state (the seeded agent's pinned model and the
///         model-provider map).
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
    private const string GeneralAgentSlug = "work-session-general";
    private const string FakeChatModel = "qwen3.5:0.8b";

    /// <summary>
    ///     Pins the fixture's chat model on the seeded General persona, which the create path REQUIRES here.
    ///     <para>
    ///         An agent that pins no model (both seeded personas ship that way) makes the service fall back to
    ///         <c>ILocalDefaultChatModelResolver</c>, and that resolver answers from installed <b>GGUF</b> models only —
    ///         never Ollama, by design, since llama.cpp is the node's default runtime. This fixture is an Ollama-only
    ///         node with no GGUF installed, so the fallback resolves to nothing and create is refused with "This node
    ///         has no chat model a work session could run on. Install one, or pin a model on the agent."
    ///     </para>
    ///     <para>
    ///         Pinning is the second half of that message, so this stays a fixture concession rather than a product
    ///         change. FakeOllama's <c>/api/show</c> advertises <c>tools</c>, so the tool-capability gate the service
    ///         applies next passes on the pinned name.
    ///     </para>
    /// </summary>
    private async Task PinChatModelOnSeededAgentAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IAgentDefinitionStore>();

        var agent = await store.GetBySeedSlugAsync(GeneralAgentSlug);
        await Assert.That(agent).IsNotNull();
        if (agent!.ModelProfile == FakeChatModel)
        {
            return;
        }

        var updated = await store.UpdateAsync(agent.Id,
            new AgentDefinitionInput(agent.Name,
                agent.Description,
                agent.Instructions,
                FakeChatModel,
                agent.ReasoningEffort,
                agent.Kind,
                agent.AllowedToolNames,
                agent.ToolApprovals,
                agent.OrchestrationTopologyJson,
                agent.PlaybookEnabled,
                agent.AllowedSkillIds,
                agent.DefaultTemporaryChat,
                agent.MemoryExtractionEnabled,
                agent.DisableBaseScaffold));

        await Assert.That(updated?.ModelProfile).IsEqualTo(FakeChatModel);
    }

    /// <summary>
    ///     Routes the fixture's chat model to the Ollama provider, which the tool-capability gate then needs.
    ///     <para>
    ///         An UNMAPPED model falls through to the node's default provider — llama.cpp — and
    ///         <c>ModelCapabilityResolver</c> answers for a llama.cpp model from the GGUF's own chat template rather
    ///         than probing Ollama. With no GGUF on disk that read yields nothing and the model reads as not
    ///         tool-capable, so create is refused a second time. On a real node an installed Ollama model carries this
    ///         mapping row; nothing writes it here because the sync worker is an <c>IHostedService</c> and the E2E host
    ///         removes those. Writing it directly is the fixture standing in for that worker.
    ///     </para>
    /// </summary>
    private async Task MapChatModelToOllamaAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var coordinator = scope.ServiceProvider.GetRequiredService<IModelProviderMapLeaseCoordinator>();
        var mapStore = scope.ServiceProvider.GetRequiredService<ICoordinatedModelProviderMapStore>();

        await using (var lease = await coordinator.AcquireMapMutationAsync(FakeChatModel, ModelProviderMapMutationKind.MapUpsert))
        {
            _ = await mapStore.TryUpsertAsync(lease, FakeChatModel, OllamaLocalModelProvider.OllamaProviderName);
        }

        // The resolver caches provider names for a TTL, so drop the entry written before this mapping existed.
        Factory.Services.GetRequiredService<ILocalModelProviderResolver>().InvalidateModelProviderMap();
    }

    [After(Test)]
    public void ResetScripts()
    {
        Factory.FakeOllamaState.ChatScript = null;
        Factory.FakeOllamaState.ToolCallScript = null;
    }

    /// <summary>
    ///     Stops at the first step rather than at <c>Completed</c>, because this fixture cannot run a server-initiated
    ///     local turn to completion. <c>NodeChatStreamService</c> resolves a send with no explicit request model
    ///     through the local-default path, which is installed-GGUF-only by design ("never Ollama") — and the supervisor
    ///     never sets a request model, so an agent's pinned <c>ModelProfile</c> is not consulted before the
    ///     <c>NoChatModelInstalledException</c> guard fires. The E2E node has FakeOllama and no GGUF, so every step
    ///     terminates as <c>StepFailed</c>. Everything this test asserts is the frontend contract and is unaffected;
    ///     the <c>update_work_plan</c> / <c>complete_work_session</c> leg needs either an installed GGUF in the fixture
    ///     or the guard to consider the agent's pin, and is tracked separately.
    /// </summary>
    [Test]
    [Category("Page")]
    public async Task Work_Session_Starts_And_Streams_Its_Lifecycle_To_The_Panes()
    {
        await PinChatModelOnSeededAgentAsync();
        await MapChatModelToOllamaAsync();
        await MapChatModelToOllamaAsync();

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
        var createResponse = await Page.RunAndWaitForResponseAsync(async () => await submit.ClickAsync(),
            response => response.Url.Contains("/api/local/v1/work-sessions", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(response.Request.Method, "POST", StringComparison.OrdinalIgnoreCase),
            new PageRunAndWaitForResponseOptions
            {
                Timeout = 10_000
            });

        // The wait above matches on URL + method, so a REFUSED create satisfies it just as well as a created one and
        // the failure would otherwise surface 10s later as a missing grid. Read the body on a non-2xx so the reason
        // (a 400 from the create validation, say) is in the failure message.
        if (createResponse.Status is < 200 or > 299)
        {
            var body = await createResponse.TextAsync();
            Assert.Fail($"Creating the work session failed with {createResponse.Status}: {body}");
        }

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

        // Start is accepted out of band (202) and the supervisor picks the session up: the badge leaves Draft and the
        // Start control goes with it. This is the live wiring under test — the hub's `status` push (or, if the hub
        // could not connect, the 3s poll fallback) drives the re-render, so it holds either way.
        await Expect(Page.GetByTestId("work-session-status-badge")).Not.ToHaveTextAsync("Draft",
            new LocatorAssertionsToHaveTextOptions
            {
                Timeout = 30_000
            });
        await Expect(Page.GetByTestId("work-session-start")).ToHaveCountAsync(0);

        // The Events tab renders the append-only feed the supervisor writes as it goes, which is the other half of the
        // live wiring: hub notification -> events query -> tab. StepStarted proves the step loop actually ran.
        await Page.GetByTestId("work-session-tab-events").ClickAsync();
        await Expect(Page.GetByTestId("work-session-events-tab")).ToContainTextAsync("StepStarted",
            new LocatorAssertionsToContainTextOptions
            {
                Timeout = 30_000
            });
    }
}
