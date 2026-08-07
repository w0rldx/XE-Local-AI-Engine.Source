namespace XE_Local_AI_Engine.Client.Services.AgentHome;

/// <summary>
///     Narrow public seam the chat agent-mode path uses to make a conversation's uploaded attachments readable by an
///     AgentHome-capable agent's file tools (<c>list_files</c> / <c>read_file</c> / <c>search_text</c>). It re-stages the
///     node sandbox selected root so it holds ONLY the given conversation's extracted attachments under the workspace
///     <c>attachments/</c> alias, with no cross-conversation residue.
///     <para>
///         Kept separate from the internal <see cref="IAgentHomeService" /> so the public
///         <c>NodeChatStreamService</c> can depend on it without an inconsistent-accessibility error;
///         <c>AgentHomeService</c> implements both over one shared singleton, so this re-stage shares the owner-node
///         execution lease with <c>run_in_agent_home</c>.
///     </para>
/// </summary>
public interface IConversationSandboxStager
{
    /// <summary>
    ///     Ensures the node sandbox is freshly staged with the conversation's extracted attachments and returns the
    ///     workspace-relative paths of the staged files (e.g. <c>attachments/report.md</c>), in staging order, so the
    ///     caller can point the model straight at them. When Agent Mode is disabled, returns an empty preparation without
    ///     touching the existing sandbox. When the owner-node lease is unavailable, returns a busy preparation. After
    ///     acquiring the lease, replaces the sandbox selected root even when the conversation has no extracted files, so
    ///     no prior project or conversation remains visible.
    /// </summary>
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
