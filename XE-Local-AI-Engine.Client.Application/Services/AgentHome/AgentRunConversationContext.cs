namespace XE_Local_AI_Engine.Client.Services.AgentHome;

/// <summary>
///     Per-root-invocation conversation context, flowed implicitly through the agent tool loop as an
///     <see cref="AsyncLocal{T}" />.
/// </summary>
/// <remarks>
///     The MAF tool surface carries no per-invocation context, and the <c>run_in_agent_home</c> request is
///     deserialized from model-supplied JSON, so threading the active conversation id — which decides whether
///     uploaded attachments are staged into the sandbox — through the tool args would make it model-forgeable. The
///     chat send seeds it here when the root tool loop begins and the gateway forwards <see cref="Current" /> into
///     the internal prepare request. Default-safe: a null <see cref="Current" /> stages no attachments.
/// </remarks>
/// <seealso cref="XE_Local_AI_Engine.Client.Services.Capacity.SpawnContext" />
public static class AgentRunConversationContext
{
    // The single ambient slot: AsyncLocal flows the value into every continuation the root tool loop awaits, the
    // AgentHome gateway included, so the gateway reads the conversation id without a parameter on the MAF surface.
    private static readonly AsyncLocal<Guid?> AmbientConversationId = new();

    /// <summary>The active conversation id for the current async flow, or <see langword="null" /> when none was seeded.</summary>
    public static Guid? Current => AmbientConversationId.Value;

    /// <summary>
    ///     Seeds the active conversation id for the current async flow and returns a scope whose disposal restores
    ///     the prior ambient value.
    /// </summary>
    /// <remarks>
    ///     Called once when a root agent tool loop begins. Disposal restores the prior value rather than clearing it,
    ///     so a nested seed cannot leak into an outer turn.
    /// </remarks>
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
