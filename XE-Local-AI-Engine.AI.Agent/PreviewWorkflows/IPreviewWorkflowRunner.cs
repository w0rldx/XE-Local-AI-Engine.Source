namespace XE_Local_AI_Engine.AI.Agent.PreviewWorkflows;

using Microsoft.Extensions.AI;

/// <summary>
///     Builds and runs an Open Canvas (Preview) workflow as a MAF graph in-process over a caller-supplied
///     <strong>node-local</strong> <see cref="IChatClient" /> (invariant #1 — the runner NEVER resolves DI; Lane C
///     resolves <c>ILocalModelProvider.CreateChatClient</c> and hands the client in). All
///     <c>Microsoft.Agents.AI.Workflows</c> usage is confined behind this seam (invariant #3).
/// </summary>
public interface IPreviewWorkflowRunner
{
    /// <summary>
    ///     Builds the workflow from <paramref name="definition" />, starts it over the supplied node-local
    ///     <paramref name="chatClient" /> (shared by every agent in the graph; the runner does NOT dispose it — the
    ///     caller owns it), and returns a live session whose <see cref="IPreviewWorkflowRunSession.WatchAsync" />
    ///     drains the run to completion/pause/failure.
    /// </summary>
    Task<IPreviewWorkflowRunSession> StartAsync(PreviewWorkflowDefinition definition,
        IChatClient chatClient,
        CancellationToken cancellationToken = default);
}

/// <summary>
///     A single in-flight Preview workflow run. Holds the underlying MAF <c>StreamingRun</c> in RAM (decision #3 —
///     session-only resume, no disk checkpoint). Disposal swallows-logs (mirrors <c>OrchestrationRunSession</c>).
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
