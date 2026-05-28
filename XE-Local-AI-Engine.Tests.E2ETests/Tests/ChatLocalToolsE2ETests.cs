namespace XE_Local_AI_Engine.Tests.E2ETests.Tests;

using Microsoft.Playwright;
using XE_Local_AI_Engine.Testing.FakeOllama;
using XE_Local_AI_Engine.Tests.E2ETests.Common;

/// <summary>
///     Browser-driven E2E for the local-tools RC feature (Lane C2 / plan §D6).
///     Verifies the full send-path tool-call flow:
///     <list type="bullet">
///         <item>Enable the local-tools toggle in the chat input area.</item>
///         <item>Send a prompt that the fake model answers with a <c>Calculate</c> tool call.</item>
///         <item>
///             <see cref="FakeOllamaState.ToolCallScript" /> returns the tool-call on the first
///             request; the <c>FunctionInvokingChatClient</c> executes the tool, appends the result,
///             and re-invokes the model.  On the second request the script returns <c>null</c> so
///             the fake model echoes the final text reply.
///         </item>
///         <item>A tool-call group appears in the chat timeline.</item>
///         <item>The assistant text reply completes (send button reverts to "Send").</item>
///     </list>
///     <para>
///         The test mutates <see cref="XENodeE2EWebApplicationFactory.FakeOllamaState" /> before
///         sending and resets it in a <c>[After(Test)]</c> hook so the shared
///         <see cref="SharedType.PerTestSession" /> factory is left clean for other tests.
///     </para>
/// </summary>
[Category("Page")]
public sealed class ChatLocalToolsE2ETests : XEE2ETestBase
{
    private const string ChatInputPlaceholder = "Type your message";
    private const string SendButtonTestId = "chat-send-button";
    private const string LocalToolsToggleTestId = "chat-local-tools-toggle";

    [After(Test)]
    public void ResetToolCallScript()
    {
        // Always clear the script so no state leaks into subsequent tests on the shared session.
        Factory.FakeOllamaState.ToolCallScript = null;
    }

    private async Task<ILocator> NavigateAndWaitForChatAsync()
    {
        await Page.GotoAsync($"{NodeAppUrl}/chat", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });

        var chatInput = Page.GetByPlaceholder(ChatInputPlaceholder);
        await Expect(chatInput).ToBeVisibleAsync();
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
        // Wire the ToolCallScript: first request → Calculate tool call; subsequent requests
        // (after FunctionInvokingChatClient appends the tool-result and re-invokes) → null →
        // falls through to the echo text path.
        // Use an atomic flag to avoid any shared-state concern: once the tool call has been
        // emitted once, every subsequent request gets null.
        var toolCallEmitted = 0;
        Factory.FakeOllamaState.ToolCallScript = _ =>
        {
            if (Interlocked.CompareExchange(ref toolCallEmitted, 1, 0) == 0)
            {
                // First call: emit the Calculate tool call.
                return new FakeOllamaToolCall
                {
                    Name = "Calculate",
                    Arguments = new
                    {
                        expression = "12*9"
                    }
                };
            }

            // Subsequent calls: fall through to the echo text reply.
            return null;
        };

        await NavigateAndWaitForChatAsync();
        await EnableLocalToolsToggleAsync();

        // Start a fresh conversation. The PerTestSession SQLite retains prior conversations from
        // the shared host; clicking "New plain chat" creates a new empty conversation and selects
        // it via TanStack client-side routing (no full navigation — WaitForURLAsync won't work).
        // Wait for a new conversation-item to appear in the list instead.
        var conversationItems = Page.Locator("[data-testid^='conversation-item-']");
        var beforeCount = await conversationItems.CountAsync();

        var newButton = Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
        {
            Name = "New plain chat"
        });
        await Expect(newButton).ToBeVisibleAsync();
        await newButton.ClickAsync();

        // Wait for the new conversation to appear in the list.
        await Expect(conversationItems)
            .ToHaveCountAsync(beforeCount + 1, new LocatorAssertionsToHaveCountOptions
            {
                Timeout = 5000
            });

        // Diagnose: confirm the host address and what VITE_API_URL the SPA bundle was compiled with.
        await Console.Out.WriteLineAsync($"[DIAG] ServerAddress={Factory.ServerAddress} Page.Url={Page.Url}").ConfigureAwait(false);
        var viteBundledUrl = await Page.EvaluateAsync<string>("window?.xeConfig?.apiUrl ?? 'not-found'").ConfigureAwait(false);
        await Console.Out.WriteLineAsync($"[DIAG] window.xeConfig.apiUrl={viteBundledUrl}").ConfigureAwait(false);

        // Capture browser console errors for diagnostics.
        var consoleErrors = new List<string>();
        Page.Console += (_, msg) =>
        {
            if (msg.Type == "error")
            {
                consoleErrors.Add(msg.Text);
            }
        };

        // Log ALL requests recorded so far (before clearing) for diagnostics.
        var allBefore = Factory.FakeOllamaState.RecordedRequests;
        await Console.Out.WriteLineAsync(
                         $"[DIAG-BEFORE] Total FakeOllama requests before clear: {allBefore.Count}, chat: {allBefore.Count(r => r.Path.Contains("/api/chat", StringComparison.OrdinalIgnoreCase))}")
                     .ConfigureAwait(false);
        foreach (var req in allBefore)
        {
            await Console.Out.WriteLineAsync($"  {req.Path} | model={req.ModelName} | msgs={req.MessageCount}")
                         .ConfigureAwait(false);
        }

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

        // Clear recorded requests so we count only those from this test's send.
        Factory.FakeOllamaState.ClearRequests();

        // Fill first — send is disabled when the input is empty.
        await chatInput.FillAsync("what's 12*9?");
        await Expect(sendButton).ToBeEnabledAsync(new LocatorAssertionsToBeEnabledOptions
        {
            Timeout = 3000
        });
        await sendButton.ClickAsync();

        // Stream started: button switches to "Stop". Wait for it to confirm the send is in-flight.
        await Expect(sendButton)
            .ToHaveTextAsync("Stop", new LocatorAssertionsToHaveTextOptions
            {
                Timeout = 5000
            });

        // Stream must complete: send button reverts to "Send" text.
        await Expect(sendButton)
            .ToHaveTextAsync("Send", new LocatorAssertionsToHaveTextOptions
            {
                Timeout = 20000
            });

        // Diagnostic: log ALL Ollama requests after stream completes (total, not just post-clear).
        var allAfter = Factory.FakeOllamaState.RecordedRequests;
        var chatRequests = allAfter
                           .Where(static r => r.Path.Contains("/api/chat", StringComparison.OrdinalIgnoreCase))
                           .ToList();
        await Console.Out.WriteLineAsync($"[DIAG] Total requests after send: {allAfter.Count}, /api/chat: {chatRequests.Count}, toolCallEmitted: {toolCallEmitted}")
                     .ConfigureAwait(false);
        foreach (var req in allAfter)
        {
            await Console.Out.WriteLineAsync($"  POST {req.Path} | model={req.ModelName} | msgs={req.MessageCount} @ {req.CapturedAtUtc:HH:mm:ss}")
                         .ConfigureAwait(false);
        }

        // Log any browser console errors.
        if (consoleErrors.Count > 0)
        {
            await Console.Out.WriteLineAsync($"[DIAG] Browser console errors ({consoleErrors.Count}):").ConfigureAwait(false);
            foreach (var err in consoleErrors)
            {
                await Console.Out.WriteLineAsync($"  {err}").ConfigureAwait(false);
            }
        }

        // After the stream completes, ChatActivityTimeline (not ToolCallDisplay) renders the persisted
        // tool-call entries. Assert the activity timeline and the Calculate entry are visible.
        await Expect(Page.GetByTestId("chat-activity-timeline"))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
            {
                Timeout = 5000
            });

        await Expect(Page.GetByTestId("chat-activity-entry-Calculate"))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
            {
                Timeout = 3000
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
