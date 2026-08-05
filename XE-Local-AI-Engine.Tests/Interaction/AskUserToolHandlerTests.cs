namespace XE_Local_AI_Engine.Tests.Interaction;

using System.Text.Json;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Services.Interaction;
using XE_Local_AI_Engine.Client.Services.Interaction.Tools.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class AskUserToolHandlerTests
{
    [Test]
    public void RequiresApproval_IsTrue()
    {
        // Structural, not a risk verdict: the approval flag is what makes FunctionInvokingChatClient END the streamed
        // segment instead of executing the tool, which is the only place the human wait can happen outside the 60 s
        // stream-idle watchdog. Flipping it moves the wait back under the watchdog and breaks the feature.
        AssertEx.True(CreateHandler(new UserQuestionAnswerStash(TimeProvider.System)).RequiresApproval,
            "ask_user must stay approval-required — that is what routes it to the out-of-stream seam");
    }

    [Test]
    public void ToolMetadata_ComesFromTheSharedDefinition()
    {
        var handler = CreateHandler(new UserQuestionAnswerStash(TimeProvider.System));

        AssertEx.Equal(AskUserTool.ToolName, handler.ToolName);
        AssertEx.Equal(AskUserTool.Description, handler.Description);
        AssertEx.Equal(AskUserTool.ParameterSchema, handler.ParameterSchema);
    }

    [Test]
    public async Task ExecuteAsync_WhenTheRunnerStashedAnAnswer_ReturnsItForTheMatchingCallId()
    {
        var stash = new UserQuestionAnswerStash(TimeProvider.System);
        var expected = UserQuestionResults.Answered([new UserQuestionAnswer("Which auth method?", ["OAuth device flow"], Other: null)]);
        stash.Stash("call-ask-user", expected);
        var handler = CreateHandler(stash);

        var result = await WithCurrentCallAsync("call-ask-user", () => handler.ExecuteAsync("{}"));

        AssertEx.Equal(expected, result);
    }

    [Test]
    public async Task ExecuteAsync_WhenNothingWasStashed_ReturnsTheNotCollectedResultRatherThanBlocking()
    {
        var handler = CreateHandler(new UserQuestionAnswerStash(TimeProvider.System));

        // The whole point of the fail-safe: a torn-down turn (or any path that reached the tool without running the
        // round-trip) must return promptly with a branchable result, never hang inside the stream-idle watchdog.
        var result = await WithCurrentCallAsync("call-never-asked", () => handler.ExecuteAsync("{}")).WaitAsync(TimeSpan.FromSeconds(5));

        AssertEx.Equal(expected: false, ReadBool(result, "answered"));
        AssertEx.Equal(UserQuestionResults.NotCollectedReason, ReadString(result, "reason"));
    }

    [Test]
    public async Task ExecuteAsync_WhenThereIsNoAmbientCall_ReturnsTheNotCollectedResult()
    {
        var stash = new UserQuestionAnswerStash(TimeProvider.System);
        stash.Stash("call-ask-user", "{\"answered\":true}");

        // No FunctionInvokingChatClient context means no correlation is possible; the stashed answer must NOT be
        // guessed at, because it may belong to a different call.
        var result = await CreateHandler(stash).ExecuteAsync("{}");

        AssertEx.Equal(expected: false, ReadBool(result, "answered"));
        AssertEx.Equal(UserQuestionResults.NotCollectedReason, ReadString(result, "reason"));
    }

    [Test]
    public async Task ExecuteAsync_WhenCalledTwiceForOneCallId_TheSecondCallGetsTheFailSafe()
    {
        var stash = new UserQuestionAnswerStash(TimeProvider.System);
        stash.Stash("call-ask-user", UserQuestionResults.Answered([new UserQuestionAnswer("Q?", ["A"], Other: null)]));
        var handler = CreateHandler(stash);

        var first = await WithCurrentCallAsync("call-ask-user", () => handler.ExecuteAsync("{}"));
        var second = await WithCurrentCallAsync("call-ask-user", () => handler.ExecuteAsync("{}"));

        AssertEx.Equal(expected: true, ReadBool(first, "answered"));
        AssertEx.Equal(expected: false, ReadBool(second, "answered"), "an answer is popped once — a replay would hand the model a stale choice");
    }

    [Test]
    public void Stash_WhenTheSameCallIdIsStashedTwice_TheLastWriteWins()
    {
        var stash = new UserQuestionAnswerStash(TimeProvider.System);
        stash.Stash("call-1", "first");
        stash.Stash("call-1", "second");

        AssertEx.True(stash.TryPop("call-1", out var popped));
        AssertEx.Equal("second", popped);
    }

    [Test]
    public void Stash_WhenAnEntryOutlivesTheRetention_ItIsSweptOnTheNextWrite()
    {
        // The entry only survives when a turn dies between the runner's stash and the tool's pop (cancel, shutdown).
        // The write-time sweep is what keeps that from accumulating in a long-lived desktop process.
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var stash = new UserQuestionAnswerStash(timeProvider);
        stash.Stash("abandoned", "orphan");

        timeProvider.Advance(TimeSpan.FromHours(1));
        stash.Stash("fresh", "kept");

        AssertEx.False(stash.TryPop("abandoned", out _), "a stale entry must be swept rather than leak for the process lifetime");
        AssertEx.True(stash.TryPop("fresh", out _));
    }

    private static AskUserToolHandler CreateHandler(UserQuestionAnswerStash stash) =>
        new(stash);

    // Runs `action` with the framework's ambient per-call context pointing at `callId`, exactly as
    // FunctionInvokingChatClient establishes it around a real tool body, and always restores the prior value.
    // The setter is protected, so it is reached through a derived client rather than by giving the handler a test
    // seam — the point of these tests is that the PRODUCTION lookup finds the right call.
    private static async Task<string> WithCurrentCallAsync(string callId, Func<Task<string>> action)
    {
        var previous = FunctionInvokingChatClient.CurrentContext;
        AmbientCallContext.Set(new FunctionInvocationContext
        {
            CallContent = new FunctionCallContent(callId, AskUserTool.ToolName)
        });

        try
        {
            return await action();
        }
        finally
        {
            AmbientCallContext.Set(previous);
        }
    }

    private static bool ReadBool(string json, string property) =>
        JsonDocument.Parse(json).RootElement.GetProperty(property).GetBoolean();

    private static string? ReadString(string json, string property) =>
        JsonDocument.Parse(json).RootElement.GetProperty(property).GetString();

    // FunctionInvokingChatClient.CurrentContext is `protected static`, so only a derived client may assign it. This
    // type is never instantiated — it exists purely to make that setter reachable from the test assembly.
    private sealed class AmbientCallContext : FunctionInvokingChatClient
    {
        private AmbientCallContext(IChatClient innerClient)
            : base(innerClient)
        {
        }

        public static void Set(FunctionInvocationContext? context) =>
            CurrentContext = context;
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public FakeTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() =>
            _utcNow;

        public void Advance(TimeSpan delta) =>
            _utcNow += delta;
    }
}
