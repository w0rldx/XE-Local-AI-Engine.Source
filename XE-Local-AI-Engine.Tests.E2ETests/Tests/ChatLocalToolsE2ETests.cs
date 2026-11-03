namespace XE_Local_AI_Engine.Tests.E2ETests.Tests;

using Microsoft.Playwright;
using XE_Local_AI_Engine.Testing.FakeOpenAiGateway;
using XE_Local_AI_Engine.Tests.E2ETests.Common;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Browser-driven E2E for the local-tools send path and tool-call flow.
///     Verifies the full send-path tool-call flow:
///     <list type="bullet">
///         <item>Enable the local-tools toggle in the chat input area.</item>
///         <item>Send a prompt that the fake model answers with a <c>Calculate</c> tool call.</item>
///         <item>
///             <see cref="FakeOpenAiGatewayState.ChatResponder" /> returns the tool call on the first request
///             offering <c>Calculate</c>; the <c>FunctionInvokingChatClient</c> executes the tool, appends the
///             result, and re-invokes the model.  The second such request parks in a gate the test releases,
///             so the stream is still open when the mid-stream assertion runs.
///         </item>
///         <item>A tool-call group appears in the chat timeline.</item>
///         <item>The assistant text reply completes (send button reverts to "Send").</item>
///     </list>
///     <para>
///         The test mutates <see cref="Infrastructure.XENodeE2EWebApplicationFactory.FakeGatewayState" /> before
///         sending and resets it in a <c>[After(Test)]</c> hook so the shared
///         <see cref="SharedType.PerTestSession" /> factory is left clean for other tests.
///     </para>
/// </summary>
[Category("Page")]
public sealed class ChatLocalToolsE2ETests : XESerialE2ETestBase
{
    private const string ChatInputPlaceholder = "Type your message";
    private const string SendButtonTestId = "chat-send-button";
    private const string LocalToolsToggleTestId = "chat-local-tools-toggle";

    // The local-tools toggle gates on the ACTIVE model advertising the `tools` capability. A fresh session
    // starts on the "Local default" sentinel, whose capabilities are unknown (treated as not tool-capable), so the
    // toggle is intentionally hidden until a concrete tool-capable model is picked. This is the fake gateway's chat
    // model (its descriptor advertises `completion` + `tools`), mirroring a real user selecting e.g. qwen.
    private const string ToolCapableModelName = FakeGatewayLocalModelProvider.ChatModel;

    // Holds the fake's post-tool answer open until the test releases it, so "the stream is in flight" is a state the
    // test creates rather than one it hopes to catch. Same shape as OnboardingTourE2ETests' tutorial-reply gate.
    private readonly TaskCompletionSource<bool> _answerGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    [After(Test)]
    public void ResetToolCallScript()
    {
        // Release first: a test that failed before its own release would otherwise leave the parked fake
        // request holding a connection on the shared session.
        _answerGate.TrySetResult(true);

        // Always clear the responder so no state leaks into subsequent tests on the shared session.
        Factory.FakeGatewayState.ChatResponder = null;
    }

    private async Task<ILocator> NavigateAndWaitForChatAsync()
    {
        await Page.GotoAsync($"{NodeAppUrl}/chat", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });

        var chatInput = Page.GetByPlaceholder(ChatInputPlaceholder);
        await Expect(chatInput).ToBeVisibleAsync();

        // Pick a concrete tool-capable model so the composer offers the local-tool controls. Without this the
        // session stays on the "Local default" sentinel (capabilities unknown → no tools toggle), mirroring a real
        // user who must select a tool-capable model before the local-tools toggle appears.
        await SelectComposerModelAsync(ToolCapableModelName);

        return chatInput;
    }

    /// <summary>
    ///     Enables the local-tools toggle by clicking it when it is not already pressed.
    ///     The button is identified by <c>data-testid="chat-local-tools-toggle"</c> and
    ///     its <c>aria-pressed</c> attribute reflects the current state.
    /// </summary>
    private async Task EnableLocalToolsToggleAsync()
    {
        var toggle = Page.GetByTestId(LocalToolsToggleTestId);
        await Expect(toggle).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
        {
            Timeout = 5000
        });

        var pressed = await toggle.GetAttributeAsync("aria-pressed");
        if (!string.Equals(pressed, "true", StringComparison.OrdinalIgnoreCase))
        {
            await toggle.ClickAsync();
            // Wait until aria-pressed flips to true.
            await Expect(toggle).ToHaveAttributeAsync("aria-pressed", "true",
                new LocatorAssertionsToHaveAttributeOptions
                {
                    Timeout = 3000
                });
        }
    }

    [Test]
    [Category("Page")]
    public async Task LocalTools_Toggle_Is_Visible_And_Toggleable()
    {
        await NavigateAndWaitForChatAsync();

        var toggle = Page.GetByTestId(LocalToolsToggleTestId);
        await Expect(toggle).ToBeVisibleAsync();

        // Toggle starts un-pressed (toolsEnabled defaults false in NodeChatPreferencesStore).
        // Click once → aria-pressed must become "true".
        await Expect(toggle).ToHaveAttributeAsync("aria-pressed", "false");
        await toggle.ClickAsync();
        await Expect(toggle).ToHaveAttributeAsync("aria-pressed", "true",
            new LocatorAssertionsToHaveAttributeOptions
            {
                Timeout = 3000
            });
    }

    [Test]
    [Category("Page")]
    public async Task Calculate_Tool_Call_Appears_In_Timeline_And_Answer_Completes()
    {
        // First request offering Calculate → the tool call; the one FunctionInvokingChatClient makes after the tool
        // result parks in this gate. Without it the whole exchange could finish before Playwright's first poll of the
        // send button, and the mid-stream assertion below failed that way once under the 4-wide parallel suite. Only a
        // request offering the tool counts: the node's own background requests offer none and fall through to the echo.
        var toolCallEmitted = 0;
        Factory.FakeGatewayState.ChatResponder = async (turn, ct) =>
        {
            if (!turn.OfferedToolNames.Contains("Calculate", StringComparer.Ordinal))
            {
                return null;
            }

            if (Interlocked.CompareExchange(ref toolCallEmitted, value: 1, comparand: 0) == 0)
            {
                return new FakeOpenAiGatewayScript
                {
                    ToolCall = new FakeOpenAiGatewayToolCall
                    {
                        Name = "Calculate",
                        Arguments = """{"expression":"12*9"}"""
                    }
                };
            }

            await _answerGate.Task.WaitAsync(ct);
            return new FakeOpenAiGatewayScript
            {
                CompletionText = "12*9 is 108"
            };
        };

        await NavigateAndWaitForChatAsync();
        await EnableLocalToolsToggleAsync();

        // Start a fresh conversation. The PerTestSession SQLite retains prior conversations from
        // the shared host; clicking "New chat" creates a new empty conversation and selects
        // it via TanStack client-side routing (no full navigation — WaitForURLAsync won't work).
        // Wait for a new conversation-item to appear in the list instead.
        var conversationItems = Page.Locator("[data-testid^='conversation-item-']");
        var beforeCount = await conversationItems.CountAsync();

        var newButton = Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
        {
            Name = "New chat",
            Exact = true
        });
        await Expect(newButton).ToBeVisibleAsync();
        await newButton.ClickAsync();

        // Wait for the new conversation to appear in the list.
        await Expect(conversationItems)
            .ToHaveCountAsync(beforeCount + 1, new LocatorAssertionsToHaveCountOptions
            {
                Timeout = 5000
            });

        var sendButton = Page.GetByTestId(SendButtonTestId);
        var chatInput = Page.GetByPlaceholder(ChatInputPlaceholder);

        // Wait for any prior stream to drain before we clear + send. If the send button shows "Stop"
        // (isSending=true from a leftover stream), clicking it would cancel that stream, not send our
        // message. Wait for text "Send" first to confirm no active stream.
        await Expect(sendButton)
            .ToHaveTextAsync("Send", new LocatorAssertionsToHaveTextOptions
            {
                Timeout = 10000
            });

        // Fill first — send is disabled when the input is empty.
        await chatInput.FillAsync("what's 12*9?");
        await Expect(sendButton).ToBeEnabledAsync(new LocatorAssertionsToBeEnabledOptions
        {
            Timeout = 3000
        });
        await sendButton.ClickAsync();

        // Stream started: button switches to "Stop". The gate guarantees the stream is still open here.
        await Expect(sendButton)
            .ToHaveTextAsync("Stop", new LocatorAssertionsToHaveTextOptions
            {
                Timeout = 5000
            });

        // Let the fake finish the answer.
        _answerGate.SetResult(true);

        // Stream must complete: send button reverts to "Send" text.
        await Expect(sendButton)
            .ToHaveTextAsync("Send", new LocatorAssertionsToHaveTextOptions
            {
                Timeout = 20000
            });

        // After the stream completes, the ordered-parts renderer persists each tool call as a
        // ToolCallCard entry (testid `chat-tool-call-card-<name>`), which replaced the former
        // ChatActivityTimeline / `chat-activity-entry-<name>` structure. The card container (its
        // summary) is always visible; the result body sits in a collapsed Collapse, so assert the
        // card, not the result.
        await Expect(Page.GetByTestId("chat-tool-call-card-Calculate"))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
            {
                Timeout = 5000
            });
    }

    [Test]
    [Category("Page")]
    public async Task Tools_Page_Lists_Both_Local_Tools()
    {
        await Page.GotoAsync($"{NodeAppUrl}/tools", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });

        await Expect(Page.GetByTestId("tools-page"))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
            {
                Timeout = 5000
            });

        await Expect(Page.GetByTestId("local-tools-overview"))
            .ToBeVisibleAsync();

        await Expect(Page.GetByTestId("local-tool-row-GetCurrentTime"))
            .ToBeVisibleAsync();

        await Expect(Page.GetByTestId("local-tool-row-Calculate"))
            .ToBeVisibleAsync();
    }
}
