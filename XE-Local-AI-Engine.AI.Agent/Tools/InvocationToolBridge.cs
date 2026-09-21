namespace XE_Local_AI_Engine.AI.Agent.Tools;

using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.AI.Agent.Tools.Implementation;

internal static class InvocationToolBridge
{
    public static AITool Create(string toolName, Func<string, CancellationToken, Task<string>> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentNullException.ThrowIfNull(handler);

        return AIFunctionFactory.Create(async (string arguments, CancellationToken cancellationToken) =>
                await handler(arguments, cancellationToken).ConfigureAwait(false),
            toolName);
    }

    /// <summary>
    ///     Creates a bridged tool that advertises the server-provided <paramref name="description" /> and
    ///     <paramref name="parameterSchema" /> to the model via <see cref="MetadataToolFunction" />.
    /// </summary>
    /// <remarks>
    ///     The handler body stays JSON-in / JSON-out and forwards the AI runtime cancellation token. With no schema it
    ///     falls back to the schema-less overload, preserving the single-argument contract.
    /// </remarks>
    public static AITool Create(string toolName,
        string? description,
        string? parameterSchema,
        Func<string, CancellationToken, Task<string>> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentNullException.ThrowIfNull(handler);

        if (string.IsNullOrWhiteSpace(parameterSchema))
        {
            return Create(toolName, handler);
        }

        return new MetadataToolFunction(toolName, description, MetadataToolFunction.ParseSchema(parameterSchema), handler);
    }

    /// <summary>
    ///     Creates a name-only offer placeholder for a tool whose executable lives in a resolution registry.
    /// </summary>
    /// <remarks>
    ///     The runtime package carries only the offer list; the invocation factory resolves the executable and
    ///     substitutes it for this placeholder before the agent runs. The placeholder THROWS if it is ever invoked,
    ///     because an offered local tool with no registry match must be dropped rather than executed.
    ///     <paramref name="requiresApproval" /> carries the resolved per-agent policy through to
    ///     <see cref="InvocationToolResolver" />, so a tightening override is honored.
    /// </remarks>
    public static AITool CreateOfferPlaceholder(string toolName, bool requiresApproval = false)
    {
        return new OfferPlaceholderAIFunction(toolName, requiresApproval);
    }
}
