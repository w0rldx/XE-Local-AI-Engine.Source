namespace XE_Local_AI_Engine.Providers.Abstractions.External;

/// <summary>
///     One external model's endpoint and trust facts, read as ONE atomic value out of ONE registry generation.
/// </summary>
/// <remarks>
///     <para>
///         It exists because reading the descriptor and the credential separately lets them come from two different
///         generations: an operator edit landing between the two reads binds a NEW key to an OLD base URL, which is the
///         shape of an accidental credential disclosure. Every consumer that both routes and authenticates therefore
///         takes one of these, not a descriptor plus a lookup.
///     </para>
///     <para>
///         <see cref="Generation" /> is the registry's monotonic snapshot epoch. It is what makes a binding
///         VERIFIABLE later: a pinned invocation re-reads the registry on every send and compares generations, so a
///         configuration edit mid-invocation is detected rather than silently applied.
///     </para>
/// </remarks>
/// <param name="Generation">The registry snapshot epoch this binding was read from. Monotonically increasing.</param>
/// <param name="Registration">The connection and model declarations, as of <paramref name="Generation" />.</param>
public sealed record ExternalProviderBinding(long Generation, ExternalProviderModelRegistration Registration)
{
    /// <summary>
    ///     The connection's FULL normalized base address — scheme, host, port AND path — as a pinned invocation
    ///     verifies it has not moved underneath the turn.
    /// </summary>
    /// <remarks>
    ///     The path is part of the address, not decoration: two OpenAI-compatible services routinely sit on one host
    ///     behind different prefixes, so comparing only the origin let an operator move a pinned turn's later sends
    ///     from <c>…/v1/</c> to <c>…/proxy/v1/</c> mid-tool-loop without the pin noticing. The value is the one the
    ///     store normalized at save time, so this compares two canonical spellings, never two operator typings.
    /// </remarks>
    public string BaseAddress => Registration.Connection.BaseUrl.AbsoluteUri;

    /// <summary>The operator-declared trust locality of the connection serving this model.</summary>
    public ExternalProviderLocality Locality => Registration.Connection.Locality;
}

/// <summary>
///     A <see cref="ExternalProviderBinding" /> plus the credential to present at that endpoint — the transport's view,
///     and the ONLY shape in which a key leaves the registry.
/// </summary>
/// <remarks>
///     The key rides WITH the endpoint it belongs to rather than being fetched beside it, so there is no arrangement of
///     concurrent edits that presents one connection's key to another connection's address. Consumers that merely
///     route, gate or render take the key-free <see cref="ExternalProviderBinding" /> (or the descriptors themselves)
///     and are structurally incapable of leaking it.
/// </remarks>
/// <param name="Binding">The endpoint and trust facts.</param>
/// <param name="ApiKey">
///     The decrypted key, or <see langword="null" /> for a keyless connection. Keyless is first-class: it means "send NO
///     <c>Authorization</c> header", never "send an empty one".
/// </param>
public sealed record ExternalProviderTransportBinding(ExternalProviderBinding Binding, string? ApiKey);
