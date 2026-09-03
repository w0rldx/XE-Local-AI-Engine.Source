namespace XE_Local_AI_Engine.Tests.E2ETests.Tests;

using Microsoft.Playwright;
using XE_Local_AI_Engine.Tests.E2ETests.Common;

/// <summary>
///     Browser-driven E2E for the Agents page (<c>/agents</c>) CRUD round-trip.
///     The existing <c>AgentsPageE2ETests</c> guards the RC null-ref typing crash and proves a create
///     reaches 201; this suite complements it by proving the FULL persistence round-trip:
///     <list type="bullet">
///         <item>Create an agent (name + instructions) → save (201) → it appears in the list BY NAME.</item>
///         <item>
///             Reopen the SAME agent via its edit control → the form repopulates the persisted
///             instructions text (proving the value survived the create → reload → fetch round-trip,
///             not just that the POST returned 201).
///         </item>
///     </list>
///     <para>
///         Why this matters: a unit/component test mocks the store, so it cannot catch a server-side
///         persistence or encryption regression (agent instructions are stored encrypted). Reopening the
///         row and reading the value back is the only layer that exercises encrypt → persist → decrypt.
///     </para>
///     <para>
///         FakeOllama is NOT needed: an agent definition is persisted config; no model call happens on save.
///         The seeded "Default Assistant" + agency-agents starter pack come from <c>DefaultAgentSeeder</c>,
///         an <c>IHostedService</c> removed by the E2E factory — so the list starts empty in this host. The
///         test asserts BY NAME against the agent it creates, never by row count.
///     </para>
///     <para>
///         Locator strategy mirrors <c>AgentsPageE2ETests</c>: Mantine <c>TextInput</c>/<c>Textarea</c> put
///         their <c>data-testid</c> on a wrapper, so the inner controls are reached via
///         <c>GetByPlaceholder</c> (Name → "Research assistant", Instructions →
///         "You are a helpful assistant that…"). The footer Save button is <c>agent-form-submit</c>.
///     </para>
/// </summary>
[Category("Page")]
public sealed class AgentsCrudE2ETests : XEPooledE2ETestBase
{
    private const string NamePlaceholder = "Research assistant";
    private const string InstructionsPlaceholder = "You are a helpful assistant that…";

    private async Task NavigateAndWaitForAgentsPageAsync()
    {
        await Page.GotoAsync($"{NodeAppUrl}/agents", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });

        var createButton = Page.GetByTestId("agent-create-button");
        await Expect(createButton).ToBeVisibleAsync();
        await Expect(createButton).ToBeEnabledAsync();
    }

    [Test]
    [Category("Page")]
    public async Task Agent_Create_Then_Reopen_Shows_Persisted_Instructions()
    {
        var pageErrors = new List<string>();
        Page.PageError += (_, error) => pageErrors.Add(error);

        await NavigateAndWaitForAgentsPageAsync();

        var agentName = $"E2E Agent {Guid.NewGuid():N}";
        var instructions = $"You are an E2E persistence probe {Guid.NewGuid():N}. Answer concisely.";

        await Page.GetByTestId("agent-create-button").ClickAsync();
        await Expect(Page.GetByTestId("agent-editor-card")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("agent-definition-form")).ToBeVisibleAsync();

        await Page.GetByPlaceholder(NamePlaceholder).FillAsync(agentName);
        await Page.GetByPlaceholder(InstructionsPlaceholder).FillAsync(instructions);

        var createResponse = await Page.RunAndWaitForResponseAsync(async () => await Page.GetByTestId("agent-form-submit").ClickAsync(),
            response => response.Url.Contains("/api/local/v1/agents", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(response.Request.Method, "POST", StringComparison.OrdinalIgnoreCase),
            new PageRunAndWaitForResponseOptions
            {
                Timeout = 10_000
            });

        await Assert.That(createResponse.Status).IsEqualTo(201);

        // The new agent appears in the list BY NAME.
        var agentCell = Page.GetByRole(AriaRole.Cell, new PageGetByRoleOptions
        {
            Name = agentName,
            Exact = true
        });
        await Expect(agentCell).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
        {
            Timeout = 5000
        });

        // The edit button carries aria-label "Edit {{name}}" (pages.agents.list.editAria).
        var editButton = Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
        {
            Name = $"Edit {agentName}"
        });
        await Expect(editButton).ToBeVisibleAsync();
        await editButton.ClickAsync();

        await Expect(Page.GetByTestId("agent-definition-form")).ToBeVisibleAsync();

        // The instructions textarea must repopulate the exact persisted value — this is the round-trip
        // proof (encrypt → persist → decrypt → fetch), which a mocked-store unit test cannot reach.
        var instructionsTextarea = Page.GetByPlaceholder(InstructionsPlaceholder);
        await Expect(instructionsTextarea).ToHaveValueAsync(instructions, new LocatorAssertionsToHaveValueOptions
        {
            Timeout = 5000
        });

        // The name field also repopulates.
        await Expect(Page.GetByPlaceholder(NamePlaceholder)).ToHaveValueAsync(agentName);

        await Assert.That(pageErrors.Count == 0).IsTrue();
    }
}
