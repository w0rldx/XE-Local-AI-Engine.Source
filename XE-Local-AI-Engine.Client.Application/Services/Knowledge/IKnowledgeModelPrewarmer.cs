namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Starts the knowledge-search companion processes (reranker, llama.cpp embedder) in the background, so a search
///     finds them warm instead of degrading to fusion order.
/// </summary>
/// <remarks>
///     Search never spawns the reranker itself; a cold one degrades to fusion. Requested once per turn that offers the
///     knowledge search tool and again after a search whose rerank came back empty. Details:
///     <c>docs/wiki/15-knowledge-base.md</c> ("Hybrid retrieval").
/// </remarks>
public interface IKnowledgeModelPrewarmer
{
    /// <summary>Requests a background warm; returns at once, coalesces with one already running, never throws.</summary>
    void RequestWarm();
}
