namespace XE_Local_AI_Engine.AI.Agent.Eval;

using Microsoft.Extensions.AI;

/// <summary>Runs the agent loop for a single golden-conversation evaluation turn.</summary>
/// <remarks>
///     The eval gate measures the effect of an injected <em>system prompt</em>, not tool behaviour, so the loop runs
///     with an empty tool set — no real side effects, no approval pauses — and threadless, with no persisted session
///     state.
/// </remarks>
public interface IPlaybookEvalAgentRunner
{
    /// <summary>
    ///     Runs the agent loop over <paramref name="inputTurns" /> with <paramref name="systemInstructions" />, and
    ///     returns the agent's final assistant text.
    /// </summary>
    /// <remarks>
    ///     Runs on the SUPPLIED chat client — node-local; this type never resolves a shared or cloud one — with an
    ///     EMPTY tool set, so there are no side effects. A supplied reasoning effort is translated through the same
    ///     matrix the production loop uses, assuming the eval model is thinking-capable, which the node's configured
    ///     chat model is; <see langword="null" /> leaves the chat options untouched, so a caller stays byte-identical.
    /// </remarks>
    /// <param name="chatClient">Node-local chat client supplied by the caller; never disposed here (caller owns it).</param>
    /// <param name="systemInstructions">The system prompt under evaluation (baseline or candidate).</param>
    /// <param name="inputTurns">The golden-conversation turns to replay through the agent.</param>
    /// <param name="reasoningEffort">Optional effort from the ordinary vocabulary, never <c>auto</c> — the dispatcher resolves that.</param>
    /// <param name="cancellationToken">Cancellation token for the run.</param>
    Task<string> RunAsync(IChatClient chatClient,
        string systemInstructions,
        IReadOnlyList<ChatMessage> inputTurns,
        string? reasoningEffort = null,
        CancellationToken cancellationToken = default);
}
