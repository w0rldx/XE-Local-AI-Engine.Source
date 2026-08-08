namespace XE_Local_AI_Engine.AI.Agent.Tools.Implementation;

using Microsoft.Extensions.AI;

/// <summary>
///     A name-only offer placeholder for a tool whose executable lives in one of the resolution registries
///     (<see cref="IAgentToolRegistry" /> / <see cref="IClientLocalToolRegistry" /> / <see cref="IMcpToolRegistry" />).
///     The runtime package carries only the offer list; <see cref="InvocationToolResolver" /> substitutes this
///     placeholder for the matching registry executable before the agent runs. It throws if it is ever invoked, because
///     an offered local tool with no registry match must be dropped rather than executed.
///     <para>
///         It also carries the resolved per-agent <see cref="RequiresApproval" /> policy so the resolver can enforce a
///         TIGHTEN-ONLY approval override: when the offer requires approval the resolver wraps the resolved executable in
///         <c>ApprovalRequiredAIFunction</c> unless it already is one. A name-only placeholder dropped this flag, which
///         silently discarded a per-agent tightening of a ClientLocal tool.
///     </para>
/// </summary>
internal sealed class OfferPlaceholderAIFunction : AIFunction
{
    public OfferPlaceholderAIFunction(string toolName, bool requiresApproval)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        Name = toolName;
        RequiresApproval = requiresApproval;
    }

    public override string Name { get; }

    /// <summary>The resolved per-agent approval policy for this offered tool; true means approval is required.</summary>
    public bool RequiresApproval { get; }

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        throw new InvalidOperationException($"Offered local tool '{Name}' has no registered executable.");
    }
}
