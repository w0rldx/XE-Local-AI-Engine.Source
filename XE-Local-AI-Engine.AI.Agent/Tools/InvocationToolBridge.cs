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
    ///     <paramref name="parameterSchema" /> to the model (via <see cref="MetadataToolFunction" />) while keeping a
    ///     JSON-in / JSON-out handler body and forwarding the AI runtime cancellation token. Falls back to the
    ///     schema-less <see cref="Create(string, Func{string, CancellationToken, Task{string}})" /> when no schema is
    ///     supplied, preserving the legacy single-argument contract.
    /// </summary>
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
    ///     Creates a name-only offer placeholder for a tool whose executable lives in a resolution registry. The runtime
    ///     package only carries the offer list (name + schema + approval flag); the executable is resolved by the
    ///     invocation factory, which substitutes this placeholder for the matching registry function before the agent
    ///     runs. The placeholder throws if it is ever invoked, because an offered local tool with no registry match must
    ///     be dropped rather than executed. <paramref name="requiresApproval" /> carries the resolved per-agent approval
    ///     policy through to <see cref="InvocationToolResolver" /> so a tightening override is honored (tighten-only) —
    ///     see <see cref="OfferPlaceholderAIFunction" />.
    /// </summary>
    public static AITool CreateOfferPlaceholder(string toolName, bool requiresApproval = false)
    {
        return new OfferPlaceholderAIFunction(toolName, requiresApproval);
    }
}
