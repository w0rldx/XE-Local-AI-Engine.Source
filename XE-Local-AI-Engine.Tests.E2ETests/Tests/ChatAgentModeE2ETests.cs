namespace XE_Local_AI_Engine.Tests.E2ETests.Tests;

using Microsoft.Playwright;
using XE_Local_AI_Engine.Tests.E2ETests.Common;

/// <summary>
///     Browser-driven E2E for chat agent-mode selection + per-response attribution (gap analysis P1-1).
///     The existing chat suite covers send/stream/cancel but never the agent picker, so a regression in
///     agent stamping (the attribution that names which persona answered) is invisible until manual testing.
///     <para>
///         Flow:
///         <list type="number">
///             <item>Create a uniquely-named agent on <c>/agents</c> (the E2E host removes the agent seeder —
///                   <c>DefaultAgentSeeder</c> is an <c>IHostedService</c> — so the agent list and the chat
///                   agent picker start empty; the picker only renders once at least one agent exists).</item>
///             <item>Go to <c>/chat</c>, open <c>AgentSelectorCard</c>, pick that agent (enabling agent mode).</item>
///             <item>Send a message — FakeOllama streams the deterministic reply.</item>
///             <item>The assistant turn's attribution row (<c>chat-message-agent-*</c>) names the picked agent.</item>
///         </list>
///     </para>
///     <para>
///         FakeOllama supplies the streamed reply via its default chat script (no <c>ChatScript</c> override is
///         required; the script is still reset in <c>[After(Test)]</c> defensively so no state leaks on the shared
///         <c>PerTestSession</c> host). The attribution text is <c>agentName · Reasoning: x · time</c>, so the test
///         asserts the chosen agent's name is a substring of the attribution element's text.
///     </para>
/// </summary>
[Category("Page")]
public sealed class ChatAgentModeE2ETests : XEE2ETestBase
{
    private const string ChatInputPlaceholder = "Type your message";
    private const string SendButtonTestId = "chat-send-button";
    private const string AgentNamePlaceholder = "Research assistant";
    private const string AgentInstructionsPlaceholder = "You are a helpful assistant that…";

    [After(Test)]
    public void ResetScripts()
    {
        Factory.FakeOllamaState.ChatScript = null;
        Factory.FakeOllamaState.ToolCallScript = null;
    }

    /// <summary>
    ///     Creates a uniquely-named agent through the /agents UI and returns its name. Reuses the same form
    ///     locators as <see cref="AgentsCrudE2ETests" /> (placeholder-targeted Mantine inputs, footer Save).
    /// </summary>
    private async Task<string> CreateAgentAsync()
    {
        var agentName = $"E2E Chat Agent {Guid.NewGuid():N}";

        await Page.GotoAsync($"{NodeAppUrl}/agents", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });

        await Page.GetByTestId("agent-create-button").ClickAsync();
        await Expect(Page.GetByTestId("agent-definition-form")).ToBeVisibleAsync();

        await Page.GetByPlaceholder(AgentNamePlaceholder).FillAsync(agentName);
        await Page.GetByPlaceholder(AgentInstructionsPlaceholder)
                  .FillAsync("You are an E2E attribution probe. Answer in one short sentence.");

        await Page.RunAndWaitForResponseAsync(
            async () => await Page.GetByTestId("agent-form-submit").ClickAsync(),
            response => response.Url.Contains("/api/local/v1/agents", StringComparison.OrdinalIgnoreCase)
                       && string.Equals(response.Request.Method, "POST", StringComparison.OrdinalIgnoreCase),
            new PageRunAndWaitForResponseOptions { Timeout = 10_000 });

        await Expect(Page.GetByRole(AriaRole.Cell, new PageGetByRoleOptions
            {
                Name = agentName,
                Exact = true
            }))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 5000 });

        return agentName;
    }

    [Test]
    [Category("Page")]
    public async Task Chat_Agent_Selection_Stamps_Per_Response_Attribution()
    {
        var agentName = await CreateAgentAsync();

        await Page.GotoAsync($"{NodeAppUrl}/chat", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });

        var chatInput = Page.GetByPlaceholder(ChatInputPlaceholder);
        var sendButton = Page.GetByTestId(SendButtonTestId);
        await Expect(chatInput).ToBeVisibleAsync();
        await Expect(sendButton).ToBeVisibleAsync();

        // The agent selector renders only because we created an agent (agentControlsAvailable =
        // showAgentControls && agentOptions.length > 0). Open it and pick our agent.
        var selectorTrigger = Page.GetByTestId("chat-agent-selector-trigger");
        await Expect(selectorTrigger).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 5000 });
        await Expect(selectorTrigger).ToBeEnabledAsync();
        await selectorTrigger.ClickAsync();

        // The picker lists the Default Assistant ("off") row plus each created agent. Pick ours by name.
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
        {
            Name = agentName
        }).First.ClickAsync();

        // The trigger label now reflects the selected agent (the merged control: picking an agent enables mode).
        await Expect(Page.GetByTestId("chat-agent-selector-selected"))
            .ToContainTextAsync(agentName, new LocatorAssertionsToContainTextOptions { Timeout = 3000 });

        // Send a message — FakeOllama streams a deterministic reply.
        await chatInput.FillAsync("Introduce yourself in one sentence.");
        await Expect(sendButton).ToBeEnabledAsync();
        await sendButton.ClickAsync();

        // Stream completes: the send button reverts to "Send".
        await Expect(sendButton).ToHaveTextAsync("Send", new LocatorAssertionsToHaveTextOptions
        {
            Timeout = 20_000
        });

        // The assistant turn's attribution row names the picked agent. The element text is
        // "agentName · Reasoning: x · time", so assert the agent name is contained in it. The attribution
        // testid carries the runtime message id, so target it by prefix.
        var attribution = Page.Locator("[data-testid^='chat-message-agent-']").Last;
        await Expect(attribution).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 5000 });
        await Expect(attribution).ToContainTextAsync(agentName, new LocatorAssertionsToContainTextOptions
        {
            Timeout = 5000
        });
    }
}
