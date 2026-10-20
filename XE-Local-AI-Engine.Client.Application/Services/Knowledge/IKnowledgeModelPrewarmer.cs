namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Starts the knowledge-search companion processes (reranker, llama.cpp embedder) in the background, so a search
///     finds them warm instead of degrading to fusion order.
/// </summary>
/// <remarks>
///     Search never spawns the reranker itself; a cold one degrades to fusion. Requested at node start, after a saved
///     reranker change, once per turn that offers the knowledge search tool, and by a search whose rerank was skipped or
///     came back empty. Details: <c>docs/wiki/15-knowledge-base.md</c> ("Pre-warm").
/// </remarks>
public interface IKnowledgeModelPrewarmer
{
    /// <summary>Requests a background warm; returns at once, coalesces with one already running, never throws.</summary>
    void RequestWarm();
}
