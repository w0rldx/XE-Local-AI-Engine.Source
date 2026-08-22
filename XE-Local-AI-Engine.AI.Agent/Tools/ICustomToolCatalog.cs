namespace XE_Local_AI_Engine.AI.Agent.Tools;

using Microsoft.Extensions.AI;

/// <summary>
///     The node's user-defined custom tool library, as the invocation stack consumes it. Mirrors the twin surface of the
///     MCP registry (<c>IMcpToolRegistry</c>): <see cref="GetDescriptorsAsync" /> feeds the offer merge, and
///     <see cref="TryResolveAsync" /> yields the executable the resolver binds to an offered name.
///     <para>
///         Unlike the MCP registry — a lock-free in-memory snapshot the connection manager refreshes — this catalog
///         reads the custom tools live from the node store on every call (no cache), so a CRUD edit takes effect on the
///         next turn without an invalidation hook. The store is DbContext-backed, so both methods are asynchronous; the
///         resolver/offer paths that consume them run in an async context.
///     </para>
///     <para>
///         SECURITY: <see cref="TryResolveAsync" /> returns the executable ALREADY wrapped in
///         <c>ApprovalRequiredAIFunction</c> — the authoritative approval floor, exactly like the MCP connection
///         manager's pre-wrap. Approval is forced on for every custom tool at the wrap, independent of any stored flag or
///         per-agent override. Delegate MCP, scheduler, and spawned-child paths never consume this catalog. The trusted
///         agentic MCP root may consume it only through its strict audit-before-invoke adapter.
///     </para>
/// </summary>
internal interface ICustomToolCatalog
{
    /// <summary>
    ///     The offer descriptors for every enabled, acknowledged custom tool (name + description + compiled GBNF-safe
    ///     schema + forced approval flag + risk category), for the offer merge. Reads the store live.
    /// </summary>
    Task<IReadOnlyList<LocalChatToolDescriptor>> GetDescriptorsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Resolves <paramref name="name" /> to its executable, already wrapped in <c>ApprovalRequiredAIFunction</c> and
    ///     the shared arg-repair + result-budget stack. Returns <see langword="null" /> when no enabled, acknowledged
    ///     custom tool has that name. Reads the store live.
    /// </summary>
    Task<AITool?> TryResolveAsync(string name, CancellationToken cancellationToken = default);
}
