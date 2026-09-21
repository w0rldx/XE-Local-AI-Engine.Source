namespace XE_Local_AI_Engine.AI.Agent.Tools;

using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;

/// <summary>
///     A dynamic registry of executable MCP tools, kept deliberately MCP-agnostic: only <see cref="AITool" />s keyed by
///     offered name plus the matching descriptors, so the MCP SDK dependency stays out of this assembly.
/// </summary>
/// <remarks>
///     The application-layer connection manager owns the MCP client lifecycle, renames each discovered tool to a
///     collision-free qualified name and pushes an immutable snapshot here; the invocation factory and the loopback
///     offer provider read it. Reads are lock-free and the snapshot is swapped atomically, so a refresh never tears a
///     concurrent read.
/// </remarks>
internal interface IMcpToolRegistry
{
    /// <summary>Resolves the executable <see cref="AITool" /> for an offered MCP tool name.</summary>
    /// <remarks>
    ///     The invocation factory consults this after the built-in and ClientLocal registries; a match returns the
    ///     cached, approval-wrapped executable, so a server-driven offer is substituted for its name-only placeholder
    ///     before the agent runs. A name in none of the three registries is dropped, skipped and warned.
    /// </remarks>
    bool TryResolve(string name, [NotNullWhen(true)] out AITool? tool);

    /// <summary>
    ///     Offer-list metadata for the currently snapshotted MCP tools: qualified name, description, JSON schema and
    ///     approval flag, which the loopback offer provider maps into transport DTOs alongside the built-in catalog.
    /// </summary>
    IReadOnlyList<LocalChatToolDescriptor> GetDescriptors();

    /// <summary>Atomically replaces the registry's snapshot with the supplied set.</summary>
    /// <remarks>
    ///     The connection manager builds the full, deterministically ordered tool list on each refresh and swaps it in
    ///     one assignment, so readers observe either the whole old snapshot or the whole new one, never a partial mix.
    /// </remarks>
    void ReplaceSnapshot(IReadOnlyList<McpRegisteredTool> tools);
}

/// <summary>
///     One registered MCP tool: the offered qualified <see cref="Name" />, the <see cref="Executable" /> (already
///     approval-wrapped when required), and the <see cref="Descriptor" /> the offer list carries.
/// </summary>
/// <remarks>The connection manager constructs these; the registry just stores them.</remarks>
internal sealed class McpRegisteredTool
{
    public required string Name { get; init; }

    public required AITool Executable { get; init; }

    public required LocalChatToolDescriptor Descriptor { get; init; }
}
