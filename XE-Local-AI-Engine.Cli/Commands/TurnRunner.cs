namespace XE_Local_AI_Engine.Cli.Commands;

using XE_Local_AI_Engine.Cli.Api;
using XE_Local_AI_Engine.Cli.Rendering;
using XE_Local_AI_Engine.Cli.Streaming;

/// <summary>
///     Plays one turn's stream into a renderer, settles parked approvals and questions through the resolve endpoints,
///     and turns Ctrl-C into <c>chat/cancel</c> followed by a bounded wait for the cancelled terminal.
/// </summary>
/// <remarks>
///     Without a prompter (no terminal, or <c>--json</c>) nothing is ever approved: an approval is denied and a
///     question cancels the turn, both with a client notice, and the outcome reports that interaction was required.
/// </remarks>
internal sealed class TurnRunner
{
    /// <summary>How long a cancelled turn may take to deliver its cancelled terminal before the client stops listening.</summary>
    internal static readonly TimeSpan CancelGrace = TimeSpan.FromSeconds(15);

    /// <summary>The client-notice kind for an approval this client denied, by policy or by the person's choice.</summary>
    internal const string ApprovalDeniedKind = "approvalDenied";

    private readonly NodeApiClient _api;
    private readonly ITurnRenderer _renderer;
    private readonly TurnPrompter? _prompter;
    private readonly TimeProvider _timeProvider;

    /// <param name="prompter">Asks the person at the terminal; null when nobody can be asked.</param>
    public TurnRunner(NodeApiClient api, ITurnRenderer renderer, TurnPrompter? prompter, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _api = api;
        _renderer = renderer;
        _prompter = prompter;
        _timeProvider = timeProvider;
    }

    private enum StopReason
    {
        User,
        InteractionRequired
    }

    /// <param name="open">Opens the stream on the token the runner controls.</param>
    /// <param name="turn">The turn's ids when known before the first frame (a send mints them), so an early Ctrl-C can cancel it.</param>
    /// <param name="interrupt">Ctrl-C: cancels the turn on the node and any open prompt.</param>
    public async Task<TurnOutcome> RunAsync(Func<CancellationToken, IAsyncEnumerable<CliStreamEvent>> open,
        CancelMessageRequest? turn,
        CancellationToken interrupt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(open);

        var state = new TurnState
        {
            Turn = turn
        };
        using var stream = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var finished = new CancellationTokenSource();
        var stopRequested = new TaskCompletionSource<StopReason>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var onInterrupt = interrupt.Register(() => stopRequested.TrySetResult(StopReason.User));
#pragma warning disable CA2025 // The finally below awaits the stopper before either token source is disposed.
        var stopper = StopWhenRequestedAsync(stopRequested.Task, state, stream, finished.Token);
#pragma warning restore CA2025
        CliStreamEvent? terminal = null;
        try
        {
            await foreach (var item in open(stream.Token).WithCancellation(stream.Token))
            {
                state.Turn = new CancelMessageRequest
                {
                    ConversationId = item.ConversationId,
                    MessageId = item.MessageId,
                    RequestId = item.RequestId
                };
                var isTerminal = item.Kind is CliStreamEventKind.Completed or CliStreamEventKind.Cancelled or CliStreamEventKind.Failed or CliStreamEventKind.Interrupted;
                if (isTerminal)
                {
                    // Like the SPA: a terminal turn dismisses an open prompt, which can no longer be answered.
                    await StopPromptAsync(state);
                    terminal = item;
                }

                await _renderer.RenderAsync(item, cancellationToken);
                switch (item.Kind)
                {
                    case CliStreamEventKind.ApprovalRequested:
                        // Also without a prompter: the deny is sent beside the loop, so Ctrl-C or a terminal abandons a stalled resolve.
                        await StartPromptAsync(state, token => ResolveApprovalAsync(item, state, token, cancellationToken), stopRequested, interrupt);
                        break;
                    case CliStreamEventKind.QuestionRequested when _prompter is null:
                        await AnswerQuestionAsync(item, state, stopRequested, CancellationToken.None, cancellationToken);
                        break;
                    case CliStreamEventKind.QuestionRequested:
                        await StartPromptAsync(state, token => AnswerQuestionAsync(item, state, stopRequested, token, cancellationToken), stopRequested, interrupt);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (stream.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // The stop grace ran out, or there was no turn yet to cancel on the node: the client stops listening.
        }
        finally
        {
            await StopPromptAsync(state);
            await finished.CancelAsync();
            await stopper;
        }

        var outcome = new TurnOutcome
        {
            Terminal = terminal,
            UserCancelled = state.UserCancelled,
            InteractionRequired = state.InteractionRequired
        };
        await _renderer.FinishAsync(outcome, cancellationToken);
        return outcome;
    }

    private async Task StopWhenRequestedAsync(Task<StopReason> requested, TurnState state, CancellationTokenSource stream, CancellationToken finished)
    {
        StopReason reason;
        try
        {
            reason = await requested.WaitAsync(finished);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        state.UserCancelled = reason == StopReason.User;
        if (state.Turn is { } turn)
        {
            // One grace bounds the cancel request and the wait for the cancelled terminal together.
            using var grace = new CancellationTokenSource(CancelGrace, _timeProvider);
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(finished, grace.Token);
            var accepted = false;
            try
            {
                await _api.CancelAsync(turn, bounded.Token);
                accepted = true;
                await Task.Delay(Timeout.InfiniteTimeSpan, bounded.Token);
            }
            catch (OperationCanceledException) when (finished.IsCancellationRequested)
            {
                // The stream ended on its own: nothing left to stop.
                return;
            }
            catch (OperationCanceledException) when (accepted)
            {
                // The grace ran out with no cancelled terminal: the client stops listening.
            }
            catch (OperationCanceledException)
            {
                await _renderer.ClientNoticeAsync("cancelFailed", "The node did not answer the cancel in time.", CancellationToken.None);
            }
            catch (Exception exception) when (exception is NodeApiException or HttpRequestException)
            {
                await _renderer.ClientNoticeAsync("cancelFailed", $"The node did not accept the cancel: {exception.Message}", CancellationToken.None);
            }
        }

        await stream.CancelAsync();
    }

    /// <summary>Opens a prompt beside the event loop, so a terminal, a cancel or a notice is still read while it waits.</summary>
    private async Task StartPromptAsync(TurnState state, Func<CancellationToken, Task> prompt, TaskCompletionSource<StopReason> stopRequested, CancellationToken interrupt)
    {
        // The node parks one interaction at a time; a newer one supersedes any still open.
        await StopPromptAsync(state);
        state.PromptCancellation = CancellationTokenSource.CreateLinkedTokenSource(interrupt);
        // Captured now: a terminal in the same batch can stop the prompt, clearing the field, before the task starts.
        var token = state.PromptCancellation.Token;
        state.Prompt = Task.Run(() => RunPromptAsync(prompt, stopRequested, token), CancellationToken.None);
    }

    /// <summary>
    ///     Runs a prompt; one that fails leaves the interaction parked until the node times it out, so it is reported
    ///     here, once, and the turn is cancelled on the node right away.
    /// </summary>
    private async Task RunPromptAsync(Func<CancellationToken, Task> prompt, TaskCompletionSource<StopReason> stopRequested, CancellationToken cancellationToken)
    {
        try
        {
            await prompt(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            try
            {
                await _renderer.ClientNoticeAsync("promptFailed", $"The prompt failed: {exception.Message}; cancelling the turn.", CancellationToken.None);
            }
            finally
            {
                // Not the person's cancel: the outcome must not report one.
                stopRequested.TrySetResult(StopReason.InteractionRequired);
            }
        }
    }

    /// <summary>Cancels the open prompt, if any, so its answer is never sent, and waits for it to wind down.</summary>
    private static async Task StopPromptAsync(TurnState state)
    {
        if (state.Prompt is not { } prompt || state.PromptCancellation is not { } cancellation)
        {
            return;
        }

        state.Prompt = null;
        state.PromptCancellation = null;
        using (cancellation)
        {
            await cancellation.CancelAsync();
            try
            {
                await prompt;
            }
            catch (OperationCanceledException)
            {
                // Dismissed, or Ctrl-C: nothing is sent for it.
            }
        }
    }

    private async Task ResolveApprovalAsync(CliStreamEvent item, TurnState state, CancellationToken prompt, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(item.Source.ApprovalRequestId))
        {
            await _renderer.ClientNoticeAsync("approvalUnresolvable", "The approval request carries no id, so this client cannot answer it.", cancellationToken);
            return;
        }

        ResolveApprovalRequest decision;
        if (_prompter is null)
        {
            state.InteractionRequired = true;
            decision = TurnPrompter.ToApprovalRequest(item, ApprovalChoice.Deny);
            await _renderer.ClientNoticeAsync(ApprovalDeniedKind,
                $"Denied tool '{item.Source.ToolName}': approving a tool needs an interactive terminal (xe chat).",
                cancellationToken);
        }
        else
        {
            decision = await _prompter.DecideApprovalAsync(item, prompt);
            prompt.ThrowIfCancellationRequested();
            if (!decision.Approved)
            {
                await _renderer.ClientNoticeAsync(ApprovalDeniedKind, $"Denied tool '{item.Source.ToolName}'.", cancellationToken);
            }
        }

        // The prompt token also ends the request, so a terminal never waits on a stalled resolve.
        using var send = CancellationTokenSource.CreateLinkedTokenSource(prompt, cancellationToken);
        await SendResolutionAsync(() => _api.ResolveApprovalAsync(decision, send.Token), send.Token);
    }

    private async Task AnswerQuestionAsync(CliStreamEvent item,
        TurnState state,
        TaskCompletionSource<StopReason> stopRequested,
        CancellationToken prompt,
        CancellationToken cancellationToken)
    {
        if (_prompter is null)
        {
            state.InteractionRequired = true;
            await _renderer.ClientNoticeAsync("questionRefused",
                "The assistant asked a question that needs an interactive terminal (xe chat); cancelling the turn.",
                cancellationToken);
            stopRequested.TrySetResult(StopReason.InteractionRequired);
            return;
        }

        var answers = await _prompter.AnswerQuestionsAsync(item, prompt);
        prompt.ThrowIfCancellationRequested();
        if (answers is null)
        {
            await _renderer.ClientNoticeAsync("questionUnreadable",
                "The question could not be read; the turn waits until the node times it out. Press Ctrl-C to cancel.",
                cancellationToken);
            return;
        }

        using var send = CancellationTokenSource.CreateLinkedTokenSource(prompt, cancellationToken);
        await SendResolutionAsync(() => _api.ResolveQuestionAsync(answers, send.Token), send.Token);
    }

    private async Task SendResolutionAsync(Func<Task> send, CancellationToken cancellationToken)
    {
        try
        {
            await send();
        }
        catch (Exception exception) when (exception is NodeApiException or HttpRequestException)
        {
            await _renderer.ClientNoticeAsync("resolveFailed", $"The node did not accept the answer: {exception.Message}", cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The HTTP client's own timeout: nobody cancelled, the node just never answered.
            await _renderer.ClientNoticeAsync("resolveFailed", "The node did not answer the resolve in time.", cancellationToken);
        }
    }

    /// <summary>What the stream loop and the stopper share; the stopper runs on the Ctrl-C thread.</summary>
    private sealed class TurnState
    {
        /// <summary>The open approval or question prompt; only the stream loop touches it.</summary>
        public Task? Prompt { get; set; }

        public CancellationTokenSource? PromptCancellation { get; set; }

        private volatile CancelMessageRequest? _turn;
        private volatile bool _userCancelled;
        private volatile bool _interactionRequired;

        public CancelMessageRequest? Turn
        {
            get => _turn;
            set => _turn = value;
        }

        public bool UserCancelled
        {
            get => _userCancelled;
            set => _userCancelled = value;
        }

        public bool InteractionRequired
        {
            get => _interactionRequired;
            set => _interactionRequired = value;
        }
    }
}
