namespace XE_Local_AI_Engine.Tests.Testing;

using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;

/// <summary>
///     Builds a <see cref="NodeChatInvocationPump" /> for tests that exercise persistence/streaming behaviour. The pump
///     no longer owns the run-envelope write (it rides into the terminalize persistence command), so it needs only the
///     persistence service and a clock.
/// </summary>
internal static class ChatPumpTestFactory
{
    public static NodeChatInvocationPump Create(INodeChatPersistenceService persistence)
    {
        return new NodeChatInvocationPump(persistence, TimeProvider.System);
    }
}
