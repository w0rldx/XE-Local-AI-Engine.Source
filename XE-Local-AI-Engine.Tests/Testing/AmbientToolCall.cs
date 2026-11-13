namespace XE_Local_AI_Engine.Tests.Testing;

using Microsoft.Extensions.AI;

/// <summary>
///     Points <see cref="FunctionInvokingChatClient.CurrentContext" /> at a tool call, exactly as the function invoker does
///     around a real tool body, until disposed; the setter is protected, so it is reached through a derived client.
/// </summary>
internal static class AmbientToolCall
{
    public static IDisposable Enter(string callId, string toolName)
    {
        var previous = Setter.Current;
        Setter.Set(new FunctionInvocationContext
        {
            CallContent = new FunctionCallContent(callId, toolName)
        });
        return new Restore(previous);
    }

    private sealed class Restore : IDisposable
    {
        private readonly FunctionInvocationContext? _previous;

        public Restore(FunctionInvocationContext? previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            Setter.Set(_previous);
        }
    }

    // Never instantiated: it exists only to make the protected setter reachable.
    private sealed class Setter : FunctionInvokingChatClient
    {
        private Setter(IChatClient innerClient)
            : base(innerClient)
        {
        }

        public static FunctionInvocationContext? Current => CurrentContext;

        public static void Set(FunctionInvocationContext? context) =>
            CurrentContext = context;
    }
}
