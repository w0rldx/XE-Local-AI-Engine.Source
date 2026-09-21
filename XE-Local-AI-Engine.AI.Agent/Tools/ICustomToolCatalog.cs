namespace XE_Local_AI_Engine.AI.Agent.Tools;

using Microsoft.Extensions.AI;

/// <summary>
///     The node's user-defined custom tool library, as the invocation stack consumes it, mirroring the twin surface of
///     the MCP registry: descriptors feed the offer merge, resolution yields the executables.
/// </summary>
/// <remarks>
///     Unlike the MCP registry's refreshed snapshot, this reads the store live on every call with no cache, so a CRUD
///     edit takes effect on the next turn; the store is DbContext-backed, hence async. SECURITY:
///     <see cref="TryResolveManyAsync" /> returns executables ALREADY wrapped in <c>ApprovalRequiredAIFunction</c>, the
///     authoritative approval floor, forced on independent of any stored flag or per-agent override. See
///     docs/wiki/04-agent-mode.md ("Custom Tools execution boundary").
/// </remarks>
internal interface ICustomToolCatalog
{
    /// <summary>
    ///     The offer descriptors for every enabled, acknowledged custom tool — name, description, compiled GBNF-safe
    ///     schema, forced approval flag, risk category — read live from the store.
    /// </summary>
    Task<IReadOnlyList<LocalChatToolDescriptor>> GetDescriptorsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Resolves every name in <paramref name="names" /> to its executable — already wrapped in
    ///     <c>ApprovalRequiredAIFunction</c> and the shared arg-repair and result-budget stack — in ONE live store read.
    /// </summary>
    /// <remarks>
    ///     Batching bounds the reads within a single operation; it is not a cache across operations. The ordinal-keyed
    ///     dictionary is never <see langword="null" />, and a name no enabled, acknowledged tool satisfies is simply
    ///     ABSENT. An empty <paramref name="names" />, or a node kill switch that is off, yields an empty dictionary
    ///     without reading the store; blank entries are tolerated and match nothing. Throws
    ///     <see cref="ArgumentNullException" /> only when <paramref name="names" /> itself is <see langword="null" />.
    /// </remarks>
    Task<IReadOnlyDictionary<string, AITool>> TryResolveManyAsync(IReadOnlyCollection<string> names,
        CancellationToken cancellationToken = default);
}
