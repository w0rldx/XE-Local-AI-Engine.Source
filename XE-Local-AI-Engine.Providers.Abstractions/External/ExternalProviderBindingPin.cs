namespace XE_Local_AI_Engine.Providers.Abstractions.External;

/// <summary>
///     The binding facts a logical invocation was AUTHORIZED against: the registry generation, the declared locality,
///     and the endpoint origin, as they stood when the turn's tools were offered.
/// </summary>
/// <remarks>
///     Tool authorization happens once, before the first send, and a multi-round tool invocation then sends several
///     times. Between two of those sends an operator can edit the connection — flipping it Local→Cloud, or moving it to
///     another host — and the later sends would carry the ALREADY-authorized local tools, and their results, to an
///     endpoint that never earned them. Pinning these three facts and re-checking them on every send is what turns that
///     silent redirect into a refused send.
/// </remarks>
/// <param name="ModelId">The namespaced <c>ext:{connectionId}/{wireId}</c> id this pin authorizes.</param>
/// <param name="Generation">The registry generation the authorization decision was made against.</param>
/// <param name="Locality">The declared locality the tool gate saw.</param>
/// <param name="Origin">The endpoint origin (scheme, host, port) the prompt was authorized to reach.</param>
public sealed record ExternalProviderBindingPin(string ModelId, long Generation, ExternalProviderLocality Locality, string Origin)
{
    /// <summary>
    ///     Whether <paramref name="binding" /> still matches what this pin authorized. Compares all three facts, not
    ///     just the generation: a generation bump caused by an unrelated connection's edit must not abort a turn, while
    ///     a locality or origin change must abort it even if the generation somehow did not move.
    /// </summary>
    public bool Matches(ExternalProviderBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);

        return binding.Generation == Generation
               || (binding.Locality == Locality && string.Equals(binding.Origin, Origin, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
///     The ambient <see cref="ExternalProviderBindingPin" />s for the current logical invocation, flowed through the
///     agent tool loop as an <see cref="AsyncLocal{T}" />.
/// </summary>
/// <remarks>
///     <para>
///         An <see cref="AsyncLocal{T}" /> rather than a parameter for the same reason <c>SpawnContext</c> is one: the
///         value has to reach the provider through MAF's function-invocation pipeline and its <c>IChatClient</c> chain,
///         neither of which carries a per-invocation context the node controls.
///     </para>
///     <para>
///         Pins are ADDITIVE and looked up by model id, because one invocation legitimately involves more than one
///         model — a sub-agent child runs inside the parent's async flow with its own binding. A send whose model has
///         no pin is not an error: it is a non-turn context (a health probe, a background summarization), which
///         resolves live under the transport's own weaker check.
///     </para>
/// </remarks>
public static class ExternalProviderBindingPinScope
{
    private static readonly AsyncLocal<IReadOnlyList<ExternalProviderBindingPin>?> AmbientPins = new();

    /// <summary>The pins in force for the current async flow, or an empty list when none was seeded.</summary>
    public static IReadOnlyList<ExternalProviderBindingPin> Current => AmbientPins.Value ?? [];

    /// <summary>
    ///     Adds <paramref name="pin" /> to the ambient set for the current async flow and returns a scope that restores
    ///     the previous set on disposal. Re-entrant: a nested scope stacks rather than replaces, so a sub-agent's pin
    ///     never evicts its parent's.
    /// </summary>
    public static IDisposable Begin(ExternalProviderBindingPin pin)
    {
        ArgumentNullException.ThrowIfNull(pin);

        var previous = AmbientPins.Value;
        AmbientPins.Value = previous is null ? [pin] : [.. previous, pin];
        return new PinScope(previous);
    }

    /// <summary>
    ///     The pin authorizing <paramref name="modelId" />, or <see langword="null" /> when this send is not part of a
    ///     pinned invocation. The MOST RECENT matching pin wins, so an inner scope's binding governs its own sends.
    /// </summary>
    public static ExternalProviderBindingPin? Find(string? modelId)
    {
        if (modelId is null || AmbientPins.Value is not { Count: > 0 } pins)
        {
            return null;
        }

        for (var index = pins.Count - 1; index >= 0; index--)
        {
            if (string.Equals(pins[index].ModelId, modelId, StringComparison.Ordinal))
            {
                return pins[index];
            }
        }

        return null;
    }

    private sealed class PinScope(IReadOnlyList<ExternalProviderBindingPin>? previous) : IDisposable
    {
        public void Dispose()
        {
            AmbientPins.Value = previous;
        }
    }
}
