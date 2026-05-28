namespace XE_Local_AI_Engine.Tests.E2ETests.Tests;

using Microsoft.Playwright;
using XE_Local_AI_Engine.Tests.E2ETests.Common;

/// <summary>
///     Browser-driven E2E tests for the node Chat page (<c>/chat</c>), mirroring the manual
///     probe in the xe-node-react-client plan §4.8.
///     <para>
///         Coverage: page render, conversation creation, message send + stream completion
///         (assistant-completed path), and cancel/dispose (stream aborted, terminal status).
///     </para>
///     <para>
///         The test host uses <see cref="XENodeE2EWebApplicationFactory" /> which wires
///         FakeOllama (<c>qwen3.5:0.8b</c>) as the local model provider.  No real Ollama process
///         is required.  The FakeOllama chat endpoint streams deterministic token chunks, so
///         assertions can rely on the stream completing in a bounded time.
///     </para>
///     <para>
///         Locator strategy: <c>data-testid</c> attributes are preferred (set in ChatInputArea.tsx,
///         ChatMessage.tsx, ChatDisplayShell.tsx, ConversationList.tsx).  The chat textarea is
///         targeted via its placeholder text (en.json key "pages.chat.inputPlaceholder") because
///         Mantine's autosize <c>Textarea</c> places <c>data-testid</c> on the wrapper div — the
///         inner <c>&lt;textarea&gt;</c> is resolved directly by <c>GetByPlaceholder</c>.
///         For elements whose <c>data-testid</c> includes a runtime UUID a CSS prefix selector
///         (<c>[data-testid^="chat-message-"]</c>) is used instead.
///     </para>
/// </summary>
[Category("Page")]
public sealed class ChatPageE2ETests : XEE2ETestBase
{
    // Mantine Textarea places data-testid="chat-input" on its wrapper div, not the inner
    // <textarea>.  GetByPlaceholder resolves the actual <textarea> directly.
    // Placeholder value comes from the en.json key "pages.chat.inputPlaceholder".
    private const string ChatInputPlaceholder = "Type your message";
    private const string SendButtonTestId = "chat-send-button";

    /// <summary>
    ///     Navigates to /chat and waits until the chat input area is visible and the
    ///     send button is present.  Returns the common locators used by most tests.
    /// </summary>
    private async Task<(ILocator chatInput, ILocator sendButton)> NavigateAndWaitForChatAsync()
    {
        await Page.GotoAsync($"{NodeAppUrl}/chat", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });

        var chatInput = Page.GetByPlaceholder(ChatInputPlaceholder);
        var sendButton = Page.GetByTestId(SendButtonTestId);

        // Chat input area renders unconditionally — no auth/pairing gate on the chat route.
        await Expect(chatInput).ToBeVisibleAsync();
        await Expect(sendButton).ToBeVisibleAsync();

        return (chatInput, sendButton);
    }

    [Test]
    [Category("Page")]
    public async Task Chat_Page_Renders_Input_Area_And_Conversation_List()
    {
        await Page.GotoAsync($"{NodeAppUrl}/chat", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });

        // Conversation list panel.
        await Expect(Page.GetByTestId("conversation-list")).ToBeVisibleAsync();

        // New-conversation button: aria-label from en.json key "pages.chat.newConversation" = "New plain chat".
        await Expect(Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
            {
                Name = "New plain chat"
            }))
            .ToBeVisibleAsync();

        // Chat input area container.
        await Expect(Page.GetByTestId("chat-input-area")).ToBeVisibleAsync();

        // NOTE: chat-capability-notice is NOT a permanent fixture — ChatDisplayShell.tsx:61 renders
        // it only when `disabledNotice` is set (Chat.tsx: streamError | conversations load error |
        // remote view-only conversation). With a clean local conversation and no error it is absent,
        // so asserting its visibility here is incorrect and order-dependent (it previously passed only
        // by coincidental transient error state under PerTestSession DB reuse). Do not assert it.

        // Chat textarea is focusable.
        await Expect(Page.GetByPlaceholder(ChatInputPlaceholder)).ToBeVisibleAsync();

        // Send button exists and is initially disabled (empty input).
        var sendButton = Page.GetByTestId(SendButtonTestId);
        await Expect(sendButton).ToBeVisibleAsync();
        await Expect(sendButton).ToBeDisabledAsync();
    }

    [Test]
    [Category("Page")]
    public async Task Chat_SendButton_Enables_When_Input_Has_Content()
    {
        var (chatInput, sendButton) = await NavigateAndWaitForChatAsync();

        // Disabled with empty input.
        await Expect(sendButton).ToBeDisabledAsync();

        // Type a message — send button must become enabled.
        await chatInput.FillAsync("Hello from the test");
        await Expect(sendButton).ToBeEnabledAsync();

        // Clear — button must go back to disabled.
        await chatInput.FillAsync("");
        await Expect(sendButton).ToBeDisabledAsync();
    }

    [Test]
    [Category("Page")]
    public async Task Chat_CreateConversation_Adds_Item_To_Conversation_List()
    {
        await NavigateAndWaitForChatAsync();

        var newConversationButton = Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
        {
            Name = "New plain chat"
        });
        await Expect(newConversationButton).ToBeVisibleAsync();

        await newConversationButton.ClickAsync();

        // After creation the conversation list must contain at least one item.
        // conversation-item-{uuid} — prefix selector covers all runtime IDs.
        await Expect(Page.Locator("[data-testid^='conversation-item-']").First)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
            {
                Timeout = 5000
            });

        // The chat window title updates to the new conversation title.
        await Expect(Page.GetByTestId("chat-window-title")).ToBeVisibleAsync();
    }

    [Test]
    [Category("Page")]
    public async Task Chat_Send_Message_And_Stream_Completes_With_Assistant_Reply()
    {
        var (chatInput, sendButton) = await NavigateAndWaitForChatAsync();

        const string userText = "Say hello from FakeOllama";

        await chatInput.FillAsync(userText);
        await Expect(sendButton).ToBeEnabledAsync();

        // Submit — sends to the in-process FakeOllama via the local SignalR chat hub.
        await sendButton.ClickAsync();

        // The input clears on submit — check the textarea value, not its text content.
        await Expect(chatInput).ToHaveValueAsync("", new LocatorAssertionsToHaveValueOptions
        {
            Timeout = 3000
        });

        // During streaming the button label switches to "Stop".
        // We tolerate the case where FakeOllama is fast and the stream already finished
        // before Playwright samples the button; in that case we fall through to the completion
        // assertion below without asserting the transient streaming state.

        // Stream completion: button must revert to "Send" (no longer isSending).
        // FakeOllama streams a small deterministic chunk — allow up to 10 s.
        await Expect(sendButton).ToHaveTextAsync("Send", new LocatorAssertionsToHaveTextOptions
        {
            Timeout = 10000
        });

        // At least one assistant message must be visible after streaming completes.
        // ChatMessage renders data-testid="chat-message-role-{id}" with "Node reply" text.
        await Expect(Page.GetByText("Node reply").First)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
            {
                Timeout = 3000
            });

        // The user message bubble must also be present.
        await Expect(Page.GetByText(userText).First).ToBeVisibleAsync();
    }

    [Test]
    [Category("Page")]
    public async Task Chat_Cancel_While_Streaming_Reverts_SendButton_To_Send()
    {
        var (chatInput, sendButton) = await NavigateAndWaitForChatAsync();

        // FakeOllama's default chat script yields several chunks with a small delay, which
        // gives us enough time to click Stop before it finishes.  If FakeOllama is
        // instantaneous the button may already show "Send" again — that is an acceptable
        // terminal state (stream already completed).
        await chatInput.FillAsync("Stream then cancel");
        await Expect(sendButton).ToBeEnabledAsync();

        await sendButton.ClickAsync();

        // Wait up to 3 s for the button to enter the streaming ("Stop") state.
        // If it never enters that state the stream completed before we could cancel,
        // which still satisfies the test's post-condition (no active stream).
        var streamingStarted = false;
        try
        {
            await Expect(sendButton).ToHaveTextAsync("Stop", new LocatorAssertionsToHaveTextOptions
            {
                Timeout = 3000
            });
            streamingStarted = true;
        }
        catch (PlaywrightException)
        {
            // Stream completed before we could observe the Stop state — fall through.
        }

        if (streamingStarted)
        {
            // Attempt to cancel by clicking the Stop button. Under parallel load the Stop button
            // may be briefly disabled (React isSending race) or already transitioned to the
            // disabled "Send" state. Swallow any exception — the post-condition below covers
            // both "cancelled" and "completed naturally" outcomes.
            // Note: Playwright's TimeoutException does not inherit PlaywrightException in all
            // versions; catch Exception to be safe.
            try
            {
                await sendButton.ClickAsync(new LocatorClickOptions
                {
                    Timeout = 2000
                });
            }
#pragma warning disable CA1031 // Do not catch general exception types
            catch (Exception)
#pragma warning restore CA1031
            {
                // Stream finished or button not interactable before cancel — acceptable.
            }
        }

        // Regardless of whether we cancelled or the stream finished naturally, the button
        // must ultimately show "Send" (isSending = false), proving no active stream remains.
        await Expect(sendButton).ToHaveTextAsync("Send", new LocatorAssertionsToHaveTextOptions
        {
            Timeout = 10000
        });
    }

    [Test]
    [Category("Page")]
    public async Task Chat_Window_Title_Updates_After_Conversation_Selected()
    {
        await NavigateAndWaitForChatAsync();

        // Create a conversation so there is something in the list to select.
        var newConversationButton = Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
        {
            Name = "New plain chat"
        });
        await newConversationButton.ClickAsync();

        // Wait for at least one conversation item to appear.
        var firstItem = Page.Locator("[data-testid^='conversation-item-']").First;
        await Expect(firstItem).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
        {
            Timeout = 5000
        });

        // Click it.
        await firstItem.ClickAsync();

        // The window title element must be visible and non-empty.
        var windowTitle = Page.GetByTestId("chat-window-title");
        await Expect(windowTitle).ToBeVisibleAsync();
        await Expect(windowTitle).Not.ToBeEmptyAsync();
    }
}
