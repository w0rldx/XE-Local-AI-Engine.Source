namespace XE_Local_AI_Engine.Client.Services.Interaction.Tools.Implementation;

using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.AI.Agent.Tools;

/// <summary>
///     <see cref="IClientLocalToolHandler" /> for <c>ask_user</c> (ClientLocal): the agent's way to put a
///     multiple-choice question to the operator and hold its turn until the answer arrives.
///     <para>
///         <b><see cref="RequiresApproval" /> is <see langword="true" /> for a STRUCTURAL reason, not a risk one.</b>
///         Asking a question is harmless — but the approval flag is what makes
///         <see cref="Microsoft.Extensions.AI.FunctionInvokingChatClient" /> surface a
///         <c>ToolApprovalRequestContent</c> and END the streamed segment instead of executing the tool, which is the
///         only place a human wait can happen outside the 60 s stream-idle watchdog. The runner performs the round-trip
///         there and stashes the answer; this handler then runs inside the resumed segment and must return
///         IMMEDIATELY. Flipping this to <see langword="false" /> does not merely skip an approval prompt — it moves the
///         wait back under the watchdog and breaks the feature.
///     </para>
///     <para>
///         The offer declares this tool as <c>ToolCategory.ReadLocal</c>: it runs entirely on this node, touches no
///         filesystem, network or process, and its only effect is rendering a prompt in the operator's own chat. The
///         tighten-only approval policy composes on top of the structural flag above and can never unwrap it.
///     </para>
///     <para>
///         The three unattended paths (sub-agent spawn, the scheduler's saved-agent runs, and the inbound MCP server)
///         already strip every approval-required tool, so <c>ask_user</c> is absent wherever there is no human to
///         answer — for free, and for exactly the right reason.
///     </para>
/// </summary>
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

    public bool RequiresApproval => true;

    /// <summary>
    ///     How this handler learns which tool call it is executing. Defaults to the framework's ambient per-call context.
    ///     It is a settable seam purely for testability: <see cref="FunctionInvokingChatClient.CurrentContext" /> has a
    ///     non-public setter, so a test cannot stage an ambient call around a direct <see cref="ExecuteAsync" />
    ///     invocation — and this method's failure mode (handing the model an answer meant for a different call, or none
    ///     at all) is precisely what has to stay covered. Production never assigns this.
    /// </summary>
    internal Func<string?> ResolveCallId { get; set; } =
        static () => FunctionInvokingChatClient.CurrentContext?.CallContent?.CallId;

    /// <summary>
    ///     Returns the answer the runner collected for THIS tool call. Never blocks and never throws: the arguments are
    ///     ignored (the runner already parsed and validated them to build the prompt), and a missing stash entry — a
    ///     torn-down turn, or the tool reached by a path that never ran the round-trip — returns the explicit
    ///     "no answer was collected" result so the model continues instead of hanging or failing the turn.
    /// </summary>
    public Task<string> ExecuteAsync(string jsonArguments, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jsonArguments);

        cancellationToken.ThrowIfCancellationRequested();

        // The framework's ambient per-call context carries the exact FunctionCallContent being executed, so the stash
        // key matches the one the runner used when it parked on this call. No CallId (a provider that omits one) means
        // no correlation is possible, which falls through to the same fail-safe below.
        var callId = ResolveCallId();

        return Task.FromResult(callId is not null && _stash.TryPop(callId, out var resultJson)
            ? resultJson
            : UserQuestionResults.Unanswered(UserQuestionResults.NotCollectedReason));
    }
}
