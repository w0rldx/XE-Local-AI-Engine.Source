namespace XE_Local_AI_Engine.Client.Services.AgentHome;

/// <summary>
///     Per-root-invocation conversation context, flowed implicitly through the agent tool loop as an
///     <see cref="AsyncLocal{T}" />. The MAF tool surface carries no per-invocation context, and the
///     <c>run_in_agent_home</c> tool request is deserialized from model-supplied JSON — so the active conversation id
///     (which decides whether uploaded attachments are staged into the sandbox) cannot be threaded through the tool
///     args without becoming model-forgeable. Instead the chat send seeds it here when the root tool loop begins, and
///     the AgentHome gateway reads <see cref="Current" /> to forward it into the internal prepare request. Modelled on
///     <see cref="XE_Local_AI_Engine.Client.Services.Capacity.SpawnContext" />, which solves the same ambient-context
///     problem for sub-agent spawn caps.
/// </summary>
/// <remarks>
///     Default-safe: a null <see cref="Current" /> means no conversation was seeded, so no attachment staging happens.
/// </remarks>
public static class AgentRunConversationContext
{
    // The single ambient slot. AsyncLocal flows the value into every continuation the root tool loop awaits, including
    // the AgentHome tool gateway the function-invocation pipeline calls, so the gateway reads the active conversation
    // id without threading a parameter through the MAF tool surface.
    private static readonly AsyncLocal<Guid?> AmbientConversationId = new();

    /// <summary>The active conversation id for the current async flow, or <see langword="null" /> when none was seeded.</summary>
    public static Guid? Current => AmbientConversationId.Value;

    /// <summary>
    ///     Seeds the active conversation id for the current async flow and returns a scope whose disposal restores the
    ///     prior ambient value. Called once when a root agent tool loop begins; the prior value is restored rather than
    ///     cleared so a nested seed cannot leak into an outer turn.
    /// </summary>
    public static IDisposable BeginScope(Guid conversationId)
    {
        var previous = AmbientConversationId.Value;
        AmbientConversationId.Value = conversationId;
        return new Scope(previous);
    }

    // Restores the prior ambient conversation id when disposed. Idempotent: a double-dispose re-restores the same value.
    private sealed class Scope : IDisposable
    {
        private readonly Guid? _previous;

        public Scope(Guid? previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            AmbientConversationId.Value = _previous;
        }
    }
}
