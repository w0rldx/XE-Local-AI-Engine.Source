namespace XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     Produces the per-conversation SEED that KEYS the untrusted-content fence nonce around attachment context,
///     derived from a SERVER-HELD secret rather than the conversation id alone.
/// </summary>
/// <remarks>
///     The conversation id reaches clients, so a client that knew it could compute the closing marker of an
///     id-seeded fence and forge a break-out. A secret the client never sees keeps the fence un-forgeable while
///     staying STABLE per conversation, preserving llama.cpp prompt-cache prefix reuse. The framing binds the marker
///     to BOTH this seed and the fenced content, so two different attachments in one conversation get different
///     closing markers, which closes the marker-replay gap between them.
/// </remarks>
public interface IUntrustedContentFenceSeedProvider
{
    /// <summary>
    ///     A stable, high-entropy, client-unknowable seed for <paramref name="conversationId" />, to pass as the
    ///     <c>nonceSeed</c> of <c>UntrustedContentFraming.WrapDocument</c>.
    /// </summary>
    /// <remarks>One conversation always yields one seed on a node; a different node key or conversation differs.</remarks>
    string DeriveSeed(Guid conversationId);
}
