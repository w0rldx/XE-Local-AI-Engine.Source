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
}
