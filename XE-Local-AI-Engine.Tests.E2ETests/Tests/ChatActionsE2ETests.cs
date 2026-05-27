namespace XE_Local_AI_Engine.Tests.E2ETests.Tests;

using Microsoft.Playwright;
using XE_Local_AI_Engine.Tests.E2ETests.Common;

/// <summary>
///     Browser-driven E2E for the RC chat feature surface that <see cref="ChatPageE2ETests" />
///     does not exercise: per-message actions (copy / regenerate / revision-nav / branch /
///     feedback) and conversation management (rename / pin / archive / show-archived / search /
///     delete-with-confirm-skip).  These are the capabilities the RC plans shipped but had only
///     unit/component coverage for.
///     <para>
///         Same host as <see cref="ChatPageE2ETests" /> — <see cref="XENodeE2EWebApplicationFactory" />
///         wires FakeOllama as the local provider, so the assistant reply ("Node reply") streams
///         deterministically in bounded time.  Locators prefer <c>data-testid</c>; per-message and
///         per-conversation ids are runtime values so prefix/CSS selectors are used.
///     </para>
/// </summary>
[Category("Page")]
public sealed class ChatActionsE2ETests : XEE2ETestBase
{
    private const string ChatInputPlaceholder = "Type your message";
    private const string SendButtonTestId = "chat-send-button";
    private const string NewConversationButtonName = "New plain chat";

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
    ///     Sends <paramref name="text" /> and waits until the assistant stream completes
    ///     (send button reverts to "Send" and the "Node reply" bubble is visible).
    /// </summary>
    private async Task SendMessageAndAwaitReplyAsync(ILocator chatInput, string text)
    {
        var sendButton = Page.GetByTestId(SendButtonTestId);
        await chatInput.FillAsync(text);
        await Expect(sendButton).ToBeEnabledAsync();
        await sendButton.ClickAsync();

        // Stream completion: button reverts to "Send", assistant bubble present.
        await Expect(sendButton).ToHaveTextAsync("Send", new LocatorAssertionsToHaveTextOptions { Timeout = 15000 });
        await Expect(Page.GetByText("Node reply").First)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 3000 });
    }

    /// <summary>Current set of conversation ids rendered in the list.</summary>
    private async Task<HashSet<string>> ConversationIdsAsync()
    {
        var testIds = await Page.Locator("[data-testid^='conversation-item-']").EvaluateAllAsync<string[]>(
            "nodes => nodes.map(n => n.getAttribute('data-testid'))");
        return testIds.Select(id => id["conversation-item-".Length..]).ToHashSet();
    }

    /// <summary>
    ///     Creates a fresh conversation and returns its runtime id via a before/after set diff.
    ///     The shared PerTestSession server SQLite leaks conversations across tests, so locating the
    ///     new item by <c>.First</c> is unreliable (it may resolve to a leaked, possibly selected,
    ///     conversation); the set diff isolates exactly the row this call created.
    /// </summary>
    private async Task<string> CreateConversationAndGetIdAsync()
    {
        var before = await ConversationIdsAsync();

        var newButton = Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = NewConversationButtonName });
        await Expect(newButton).ToBeVisibleAsync();
        await newButton.ClickAsync();

        await Expect(Page.Locator("[data-testid^='conversation-item-']"))
            .ToHaveCountAsync(before.Count + 1, new LocatorAssertionsToHaveCountOptions { Timeout = 5000 });

        var after = await ConversationIdsAsync();
        return after.Except(before).Single();
    }

    // ---- Per-message actions -------------------------------------------------

    [Test]
    [Category("Page")]
    public async Task Assistant_Message_Exposes_Copy_And_Regenerate_Actions()
    {
        var chatInput = await NavigateAndWaitForChatAsync();
        await SendMessageAndAwaitReplyAsync(chatInput, "Expose actions");

        // The assistant message renders an actions group once streaming finishes.
        await Expect(Page.Locator("[data-testid^='chat-message-actions-']").Last)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 5000 });

        // Copy + Regenerate are icon buttons identified by aria-label (en.json keys
        // pages.chat.actions.copy / .regenerate).
        await Expect(Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Copy message" }).Last)
            .ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Regenerate response" }).Last)
            .ToBeVisibleAsync();
    }

    [Test]
    [Category("Page")]
    public async Task Regenerate_Creates_Revision_And_Shows_Revision_Nav()
    {
        var chatInput = await NavigateAndWaitForChatAsync();
        await SendMessageAndAwaitReplyAsync(chatInput, "Regenerate me");

        var sendButton = Page.GetByTestId(SendButtonTestId);
        var regenerate = Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Regenerate response" }).Last;
        await Expect(regenerate).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 5000 });
        await regenerate.ClickAsync();

        // Regenerate mints a sibling variant and re-streams; wait for completion.
        await Expect(sendButton).ToHaveTextAsync("Send", new LocatorAssertionsToHaveTextOptions { Timeout = 15000 });

        // Revision nav renders only when total > 1 — proves a second variant exists. The count
        // element reads "{active+1}/{total}"; after one regenerate that is "2/2".
        var revisionCount = Page.Locator("[data-testid^='message-revision-count-']").Last;
        await Expect(revisionCount).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 5000 });
        await Expect(revisionCount).ToContainTextAsync("/2");
    }

    [Test]
    [Category("Page")]
    public async Task Feedback_ThumbsUp_Submits_Without_Error()
    {
        var chatInput = await NavigateAndWaitForChatAsync();
        await SendMessageAndAwaitReplyAsync(chatInput, "Give feedback");

        // Feedback control (gated by conversationFeedback capability, on for the node).
        var thumbsUp = Page.Locator("[data-testid^='message-feedback-up-']").Last;
        await Expect(thumbsUp).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 5000 });
        await thumbsUp.ClickAsync();

        // Selecting a rating reveals the comment box + submit; submit must not surface an error.
        var comment = Page.Locator("[data-testid^='message-feedback-comment-']").Last;
        await Expect(comment).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 5000 });
        await comment.FillAsync("Helpful reply");

        var submit = Page.Locator("[data-testid^='message-feedback-submit-']").Last;
        await Expect(submit).ToBeEnabledAsync();
        await submit.ClickAsync();

        // No client error banner after submit.
        await Expect(Page.GetByTestId("chat-stream-error")).Not.ToBeVisibleAsync();
    }

    [Test]
    [Category("Page")]
    public async Task Branch_From_Assistant_Adds_A_Conversation()
    {
        var chatInput = await NavigateAndWaitForChatAsync();
        await SendMessageAndAwaitReplyAsync(chatInput, "Branch from here");

        var items = Page.Locator("[data-testid^='conversation-item-']");
        var beforeCount = await items.CountAsync();

        var branch = Page.Locator("[data-testid^='message-branch-']").Last;
        await Expect(branch).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 5000 });
        await branch.ClickAsync();

        // Branch clones up to the message into a NEW conversation → list grows by one.
        await Expect(items).ToHaveCountAsync(beforeCount + 1, new LocatorAssertionsToHaveCountOptions { Timeout = 10000 });
    }

    // ---- Conversation management --------------------------------------------

    [Test]
    [Category("Page")]
    public async Task Conversation_Rename_Updates_Title()
    {
        await NavigateAndWaitForChatAsync();
        var id = await CreateConversationAndGetIdAsync();

        await Page.GetByTestId($"conversation-actions-{id}").ClickAsync();
        await Page.GetByTestId($"conversation-rename-{id}").ClickAsync();

        var input = Page.GetByTestId($"conversation-rename-input-{id}");
        await Expect(input).ToBeVisibleAsync();
        await input.FillAsync("Renamed thread");
        await input.PressAsync("Enter");

        // The renamed title appears in the list.
        await Expect(Page.GetByText("Renamed thread").First)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 5000 });
    }

    [Test]
    [Category("Page")]
    public async Task Conversation_Pin_Switches_Menu_To_Unpin()
    {
        await NavigateAndWaitForChatAsync();
        var id = await CreateConversationAndGetIdAsync();

        await Page.GetByTestId($"conversation-actions-{id}").ClickAsync();
        var pin = Page.GetByTestId($"conversation-pin-{id}");
        await Expect(pin).ToHaveTextAsync("Pin");
        await pin.ClickAsync();

        // Re-open the menu — the toggle must now read "Unpin".
        await Page.GetByTestId($"conversation-actions-{id}").ClickAsync();
        await Expect(Page.GetByTestId($"conversation-pin-{id}"))
            .ToHaveTextAsync("Unpin", new LocatorAssertionsToHaveTextOptions { Timeout = 5000 });
    }

    [Test]
    [Category("Page")]
    public async Task Conversation_Archive_Then_ShowArchived_Toggles_Visibility()
    {
        await NavigateAndWaitForChatAsync();
        var id = await CreateConversationAndGetIdAsync();

        await Page.GetByTestId($"conversation-actions-{id}").ClickAsync();
        await Page.GetByTestId($"conversation-archive-{id}").ClickAsync();

        // Archived conversation drops out of the default (non-archived) list.
        await Expect(Page.GetByTestId($"conversation-item-{id}"))
            .Not.ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 5000 });

        // Flipping "show archived" brings it back.
        await Page.GetByTestId("conversation-show-archived").ClickAsync();
        await Expect(Page.GetByTestId($"conversation-item-{id}"))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 5000 });
    }

    [Test]
    [Category("Page")]
    public async Task Conversation_Search_Filters_List()
    {
        await NavigateAndWaitForChatAsync();

        // Two conversations; rename one to a unique, searchable title.
        var first = await CreateConversationAndGetIdAsync();
        await Page.GetByTestId($"conversation-actions-{first}").ClickAsync();
        await Page.GetByTestId($"conversation-rename-{first}").ClickAsync();
        var input = Page.GetByTestId($"conversation-rename-input-{first}");
        await input.FillAsync("UniqueSearchTarget");
        await input.PressAsync("Enter");
        await Expect(Page.GetByText("UniqueSearchTarget").First).ToBeVisibleAsync();

        var second = await CreateConversationAndGetIdAsync();

        // Select the renamed conversation first: Chat.tsx keeps the *selected* conversation in the
        // list regardless of the search filter (displayConversations always includes the active one),
        // so the conversation that must drop out has to be the UNSELECTED one.
        await Page.GetByTestId($"conversation-item-{first}").ClickAsync();

        // Filter to the unique title — the unselected, non-matching conversation must drop out.
        await Page.GetByTestId("conversation-search").FillAsync("UniqueSearchTarget");
        await Expect(Page.GetByTestId($"conversation-item-{first}")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId($"conversation-item-{second}"))
            .Not.ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 5000 });
    }

    [Test]
    [Category("Page")]
    public async Task Conversation_Delete_ShiftClick_Skips_Confirm_And_Removes_Item()
    {
        await NavigateAndWaitForChatAsync();
        var id = await CreateConversationAndGetIdAsync();

        await Page.GetByTestId($"conversation-actions-{id}").ClickAsync();

        // Shift-click skips the confirm dialog (deleteShiftHint), deleting immediately.
        await Page.GetByTestId($"conversation-delete-{id}")
            .ClickAsync(new LocatorClickOptions { Modifiers = [KeyboardModifier.Shift] });

        await Expect(Page.GetByTestId($"conversation-item-{id}"))
            .Not.ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 5000 });
    }
}
