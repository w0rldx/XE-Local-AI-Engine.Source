namespace XE_Local_AI_Engine.AI.Agent.Tools;

using Microsoft.Extensions.AI;

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
    ///     Creates a name-only offer placeholder for a tool whose executable lives in <c>IAgentToolRegistry</c>. The
    ///     runtime package only carries the offer list (name + schema); the executable is resolved by the invocation
    ///     factory, which substitutes this placeholder for the matching registry function before the agent runs. The
    ///     placeholder throws if it is ever invoked, because an offered local tool with no registry match must be
    ///     dropped rather than executed.
    /// </summary>
    public static AITool CreateOfferPlaceholder(string toolName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        Func<string, string> placeholder = _ =>
            throw new InvalidOperationException($"Offered local tool '{toolName}' has no registered executable.");

        return AIFunctionFactory.Create(placeholder, toolName);
    }
}
