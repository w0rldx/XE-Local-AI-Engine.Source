namespace XE_Local_AI_Engine.AI.Agent.Tools;

using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;

/// <summary>
///     A dynamic registry of executable MCP tools, kept deliberately MCP-agnostic: it holds only
///     <see cref="AITool" />s keyed by their offered name plus the matching offer-list descriptors, so the MCP SDK
///     dependency stays out of this assembly. The application-layer connection manager owns the MCP client lifecycle,
///     renames each discovered tool to a collision-free qualified name, and pushes an immutable snapshot here via
///     <see cref="ReplaceSnapshot" />; the invocation factory (MCP) and the loopback offer provider read the live
///     snapshot. Reads are lock-free; the snapshot is swapped atomically so a refresh never tears a concurrent read.
/// </summary>
internal interface IMcpToolRegistry
{
    /// <summary>
    ///     Resolves the executable <see cref="AITool" /> for an offered MCP tool name. The invocation factory consults
    ///     this after the built-in (Option A) and ClientLocal (ClientLocal) registries; a match returns the cached,
    ///     approval-wrapped executable so a server-driven offer is substituted for its name-only placeholder before the
    ///     agent runs. A name in none of the three registries is dropped (skipped + warned).
    /// </summary>
    bool TryResolve(string name, [NotNullWhen(true)] out AITool? tool);

    /// <summary>
    ///     Offer-list metadata for the currently snapshotted MCP tools (qualified name + description + JSON schema +
    ///     approval flag). The loopback offer provider maps these into transport DTOs alongside the built-in catalog.
    /// </summary>
    IReadOnlyList<LocalChatToolDescriptor> GetDescriptors();

    /// <summary>
    ///     Atomically replaces the registry's snapshot with the supplied set. The connection manager builds the full,
    ///     deterministically ordered tool list on each refresh and swaps it in one assignment, so readers observe either
    ///     the whole old snapshot or the whole new one — never a partial mix.
    /// </summary>
    void ReplaceSnapshot(IReadOnlyList<McpRegisteredTool> tools);
}

/// <summary>
///     One registered MCP tool: the offered (qualified) <paramref name="Name" />, the executable
///     <paramref name="Executable" /> (already approval-wrapped when required), and the
///     <paramref name="Descriptor" /> the offer list carries. The connection manager constructs these; the registry
///     just stores them.
/// </summary>
internal sealed record McpRegisteredTool(string Name, AITool Executable, LocalChatToolDescriptor Descriptor);
