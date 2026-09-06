namespace XE_Local_AI_Engine.AI.Agent.Tools;

using Microsoft.Extensions.AI;

/// <summary>
///     The node's user-defined custom tool library, as the invocation stack consumes it. Mirrors the twin surface of the
///     MCP registry (<c>IMcpToolRegistry</c>): <see cref="GetDescriptorsAsync" /> feeds the offer merge, and
///     <see cref="TryResolveManyAsync" /> yields the executables the resolver binds to the offered names.
///     <para>
///         Unlike the MCP registry — a lock-free in-memory snapshot the connection manager refreshes — this catalog
///         reads the custom tools live from the node store on every call (no cache), so a CRUD edit takes effect on the
///         next turn without an invalidation hook. The store is DbContext-backed, so both methods are asynchronous; the
///         resolver/offer paths that consume them run in an async context. Resolution is BATCHED so one resolution
///         operation costs one store read no matter how many custom names the offer carries; the batch bounds the reads
///         within a single operation and is not a cache across operations.
///     </para>
///     <para>
///         SECURITY: <see cref="TryResolveManyAsync" /> returns executables ALREADY wrapped in
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
    ///     Resolves every name in <paramref name="names" /> to its executable, already wrapped in
    ///     <c>ApprovalRequiredAIFunction</c> and the shared arg-repair + result-budget stack, in ONE live store read.
    ///     The returned ordinal-keyed dictionary is never <see langword="null" />; a name no enabled, acknowledged custom
    ///     tool satisfies is simply ABSENT from it, which the resolver treats exactly as it treated a null before. An
    ///     empty <paramref name="names" />, or a node-level kill switch that is off, yields an empty dictionary without
    ///     reading the store. Blank entries are tolerated and match nothing, but a <see langword="null" /> ENTRY is not:
    ///     the batch keys the requested names into an ordinal <see cref="HashSet{T}" />, so a null element throws
    ///     <see cref="ArgumentNullException" /> exactly as a null collection does. Every caller filters on the
    ///     <c>custom__</c> prefix, so no production path can supply one.
    /// </summary>
    Task<IReadOnlyDictionary<string, AITool>> TryResolveManyAsync(IReadOnlyCollection<string> names,
        CancellationToken cancellationToken = default);
}
