namespace XE_Local_AI_Engine.Client.Services.Interaction.Tools.Implementation;

using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.AI.Agent.Tools;

/// <summary>
///     <see cref="IClientLocalToolHandler" /> for <c>ask_user</c> (ClientLocal): the agent's way to put a
///     multiple-choice question to the operator and hold its turn until the answer arrives.
/// </summary>
/// <remarks>
///     <b><see cref="RequiresApproval" /> is <see langword="true" /> for a STRUCTURAL reason, not a risk one.</b> Asking a question is harmless, but the
///     approval flag is what makes <see cref="Microsoft.Extensions.AI.FunctionInvokingChatClient" /> surface a <c>ToolApprovalRequestContent</c> and END
///     the streamed segment instead of executing the tool — the only place a human wait can happen outside the 60 s stream-idle watchdog. The runner does
///     the round-trip there and stashes the answer, so this handler runs inside the resumed segment and must return IMMEDIATELY. Flipping it to
///     <see langword="false" /> does not merely skip a prompt: it moves the wait back under the watchdog and breaks the feature.
/// </remarks>
internal sealed class AskUserToolHandler : IClientLocalToolHandler
{
    private readonly UserQuestionAnswerStash _stash;

    public AskUserToolHandler(UserQuestionAnswerStash stash)
    {
        _stash = stash ?? throw new ArgumentNullException(nameof(stash));
    }

    public string ToolName => AskUserTool.ToolName;

    public string Description => AskUserTool.Description;

    public string ParameterSchema => AskUserTool.ParameterSchema;

    /// <inheritdoc />
    /// <remarks>
    ///     The offer declares this tool as <c>ToolCategory.ReadLocal</c>: it runs entirely on this node, touches no filesystem, network or process, and
    ///     its only effect is a prompt in the operator's own chat, so the tighten-only approval policy composes on top of this structural flag and can
    ///     never unwrap it. Sub-agent spawn, the scheduler's saved-agent runs and delegate-scope inbound MCP already strip every approval-required tool;
    ///     agentic-scope inbound MCP is the audited auto-approval exception, and there the no-answer fail-safe returns at once.
    /// </remarks>
    public bool RequiresApproval => true;

    /// <summary>How this handler learns which tool call it is executing. Defaults to the framework's ambient per-call context.</summary>
    /// <remarks>
    ///     It is a settable seam purely for testability: <see cref="FunctionInvokingChatClient.CurrentContext" /> has a non-public setter,
    ///     so a test cannot stage an ambient call around a direct <see cref="ExecuteAsync" /> invocation — and this method's failure mode
    ///     (handing the model an answer meant for a different call, or none at all) is precisely what has to stay covered. Production never
    ///     assigns this.
    /// </remarks>
    internal Func<string?> ResolveCallId { get; set; } =
        static () => FunctionInvokingChatClient.CurrentContext?.CallContent?.CallId;

    /// <summary>Returns the answer the runner collected for THIS tool call.</summary>
    /// <remarks>
    ///     Never blocks and never throws: the arguments are ignored (the runner already parsed and validated them to build the prompt), and
    ///     a missing stash entry — a torn-down turn, or the tool reached by a path that never ran the round-trip — returns the explicit "no
    ///     answer was collected" result so the model continues instead of hanging or failing the turn.
    /// </remarks>
    public Task<string> ExecuteAsync(string jsonArguments, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jsonArguments);

        cancellationToken.ThrowIfCancellationRequested();

        // The framework's ambient per-call context carries the exact FunctionCallContent being executed, so the stash key matches the one the runner used when
        // it parked on this call. No CallId (a provider that omits one) means no correlation is possible, which falls through to the same fail-safe below.
        var callId = ResolveCallId();

        return Task.FromResult(callId is not null && _stash.TryPop(callId, out var resultJson)
            ? resultJson
            : UserQuestionResults.Unanswered(UserQuestionResults.NotCollectedReason));
    }
}
