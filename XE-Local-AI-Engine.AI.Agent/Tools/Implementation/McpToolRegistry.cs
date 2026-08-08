namespace XE_Local_AI_Engine.AI.Agent.Tools.Implementation;

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

/// <summary>
///     Lock-free dynamic registry of MCP tools backed by a single immutable snapshot. The connection manager rebuilds
///     the entire tool set on each refresh and calls <see cref="ReplaceSnapshot" />, which swaps the
///     <see langword="volatile" /> snapshot reference in one assignment. Readers (<see cref="TryResolve" /> /
///     <see cref="GetDescriptors" />) capture the reference once and read from the immutable instance, so they observe a
///     consistent point-in-time view without locking and a refresh can never tear a concurrent read.
/// </summary>
internal sealed class McpToolRegistry : IMcpToolRegistry
{
    private readonly ILogger<McpToolRegistry> _logger;
    private volatile Snapshot _snapshot = Snapshot.Empty;

    public McpToolRegistry(ILogger<McpToolRegistry> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool TryResolve(string name, [NotNullWhen(true)] out AITool? tool)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return _snapshot.Executables.TryGetValue(name, out tool);
    }

    public IReadOnlyList<LocalChatToolDescriptor> GetDescriptors()
    {
        return _snapshot.Descriptors;
    }

    public void ReplaceSnapshot(IReadOnlyList<McpRegisteredTool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);

        var executables = ImmutableDictionary.CreateBuilder<string, AITool>(StringComparer.Ordinal);
        var descriptors = ImmutableArray.CreateBuilder<LocalChatToolDescriptor>(tools.Count);

        foreach (var tool in tools)
        {
            // A duplicate qualified name should never reach here (server slugs are unique and MCP guarantees unique
            // tool names within a server). Guard defensively in LOCKSTEP: add the descriptor only when the executable
            // key is new, so the offered descriptor list can never advertise a tool whose executable a later duplicate
            // overwrote (which would leave N descriptors against N-1 executables). First write wins for both.
            if (executables.ContainsKey(tool.Name))
            {
                _logger.LogWarning("Duplicate MCP tool name {ToolName} in snapshot; keeping the first and dropping the duplicate descriptor.", tool.Name);
                continue;
            }

            executables[tool.Name] = tool.Executable;
            descriptors.Add(tool.Descriptor);
        }

        _snapshot = new Snapshot(executables.ToImmutable(), descriptors.ToImmutable());
    }

    private sealed record Snapshot(ImmutableDictionary<string, AITool> Executables, ImmutableArray<LocalChatToolDescriptor> Descriptors)
    {
        public static Snapshot Empty { get; } = new(ImmutableDictionary<string, AITool>.Empty.WithComparers(StringComparer.Ordinal), []);
    }
}
