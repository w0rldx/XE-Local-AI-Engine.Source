namespace XE_Local_AI_Engine.AI.Agent.Eval;

using Microsoft.Extensions.AI;

/// <summary>
///     Runs the agent loop for a single Playbook P4 golden-conversation evaluation turn. The eval gate measures
///     the effect of an injected <em>system prompt</em>, not tool behaviour, so the loop runs with an empty tool
///     set (no real side effects, no approval pauses) and threadless (no persisted session state).
/// </summary>
public interface IPlaybookEvalAgentRunner
{
    /// <summary>
    ///     Runs the agent loop over <paramref name="inputTurns" /> with <paramref name="systemInstructions" />,
    ///     using the SUPPLIED chat client (the caller passes a node-local client; this type never resolves a
    ///     shared/cloud one) and an EMPTY tool set (no side effects). Returns the agent's final assistant text.
    /// </summary>
    /// <param name="chatClient">Node-local chat client supplied by the caller; never disposed here (caller owns it).</param>
    /// <param name="systemInstructions">The system prompt under evaluation (baseline or candidate).</param>
    /// <param name="inputTurns">The golden-conversation turns to replay through the agent.</param>
    /// <param name="cancellationToken">Cancellation token for the run.</param>
    Task<string> RunAsync(IChatClient chatClient,
        string systemInstructions,
        IReadOnlyList<ChatMessage> inputTurns,
        CancellationToken cancellationToken = default);
}
