namespace XE_Local_AI_Engine.AI.Agent.PreviewWorkflows;

using Microsoft.Extensions.AI;

/// <summary>
///     Builds and runs an Open Canvas (Preview) workflow as a MAF graph in-process over caller-supplied
///     <strong>node-local</strong> <see cref="IChatClient" />s (invariant: the runner NEVER resolves DI; the caller
///     resolves <c>ILocalModelProvider.CreateChatClient</c> per distinct model and hands a resolver in). All
///     <c>Microsoft.Agents.AI.Workflows</c> usage is confined behind this seam (invariant: no MAF type leaks past it).
/// </summary>
public interface IPreviewWorkflowRunner
{
    /// <summary>
    ///     Builds the workflow from <paramref name="definition" />, starts it resolving each agent node's node-local
    ///     <see cref="IChatClient" /> via <paramref name="resolveChatClient" /> (keyed by the agent node's
    ///     <c>ModelId</c>, so each agent runs on its OWN selected model; the caller is expected to return ONE shared
    ///     client per distinct model id), and returns a live session whose
    ///     <see cref="IPreviewWorkflowRunSession.WatchAsync" /> drains the run to completion/pause/failure.
    ///     The runner does NOT dispose any resolved client — the caller owns and disposes every client it hands out.
    /// </summary>
    Task<IPreviewWorkflowRunSession> StartAsync(PreviewWorkflowDefinition definition,
        Func<string, IChatClient> resolveChatClient,
        CancellationToken cancellationToken = default);
}

/// <summary>
///     A single in-flight Preview workflow run. Holds the underlying MAF <c>StreamingRun</c> in RAM
///     (session-only resume, no disk checkpoint). Disposal swallows-logs (mirrors <c>OrchestrationRunSession</c>).
/// </summary>
public interface IPreviewWorkflowRunSession : IAsyncDisposable
{
    /// <summary>
    ///     Drains the run, yielding <see cref="PreviewWorkflowUpdate" />s until the run completes, fails, or halts on a
    ///     Pause node (a <see cref="PreviewWorkflowUpdateKind.RunPaused" /> update is yielded and the enumeration then
    ///     ends; call <see cref="ResumeAsync" /> with the surfaced request id and re-enumerate to continue).
    /// </summary>
    IAsyncEnumerable<PreviewWorkflowUpdate> WatchAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Resumes a run halted at a Pause node by responding to the pause request surfaced via
    ///     <see cref="PreviewWorkflowUpdate.RequestId" />. After calling, re-enumerate <see cref="WatchAsync" /> to
    ///     pump the resumed run.
    /// </summary>
    Task ResumeAsync(string requestId, CancellationToken cancellationToken = default);
}
