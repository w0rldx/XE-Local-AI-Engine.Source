namespace XE_Local_AI_Engine.Client.Services.AgentHome;

/// <summary>
///     Narrow public seam the chat agent-mode path uses to make a conversation's uploaded attachments readable by an
///     AgentHome-capable agent's file tools (<c>list_files</c>, <c>read_file</c>, <c>search_text</c>).
/// </summary>
/// <remarks>
///     It re-stages the node sandbox selected root so it holds ONLY that conversation's extracted attachments under
///     the workspace <c>attachments/</c> alias, with no cross-conversation residue. It stays separate from the
///     internal <see cref="IAgentHomeService" /> so the public <c>NodeChatStreamService</c> can depend on it without
///     an inconsistent-accessibility error, and <c>AgentHomeService</c> implements both over one shared singleton —
///     so this re-stage shares the owner-node execution lease with <c>run_in_agent_home</c>.
/// </remarks>
public interface IConversationSandboxStager
{
    /// <summary>
    ///     Ensures the node sandbox is freshly staged with the conversation's extracted attachments and returns the
    ///     workspace-relative paths of the staged files (e.g. <c>attachments/report.md</c>), in staging order.
    /// </summary>
    /// <remarks>
    ///     The order lets the caller point the model straight at them. With Agent Mode disabled it returns an empty
    ///     preparation without touching the existing sandbox, and with the owner-node lease unavailable a busy one.
    ///     Once the lease is held it replaces the sandbox selected root even when the conversation has no extracted
    ///     files, so no prior project or conversation remains visible.
    /// </remarks>
    Task<ConversationSandboxPreparation> PrepareConversationAttachmentsAsync(Guid conversationId, CancellationToken cancellationToken = default);
}

/// <summary>
///     A staged attachment workspace plus the owner-node lease that keeps it stable until the detached agent invocation
///     has fully drained. The caller must dispose this result after both the producer and response pump complete.
/// </summary>
public sealed class ConversationSandboxPreparation : IAsyncDisposable
{
    private IAgentHomeExecutionLease? _lease;

    internal ConversationSandboxPreparation(IReadOnlyList<string> stagedPaths, IAgentHomeExecutionLease? lease, bool isBusy = false)
    {
        StagedPaths = stagedPaths;
        _lease = lease;
        IsBusy = isBusy;
    }

    public IReadOnlyList<string> StagedPaths { get; }

    public bool IsBusy { get; }

    /// <summary>
    ///     Marks the caller's current execution context as the owner of this preparation while it creates the detached
    ///     invocation tasks. Those tasks inherit the marker and may borrow the same-key lease for Coder reads.
    /// </summary>
    public IDisposable EnterInvocationScope()
    {
        return _lease?.EnterAmbientScope() ?? EmptyScope.Instance;
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _lease, null)?.Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed class EmptyScope : IDisposable
    {
        public static EmptyScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
