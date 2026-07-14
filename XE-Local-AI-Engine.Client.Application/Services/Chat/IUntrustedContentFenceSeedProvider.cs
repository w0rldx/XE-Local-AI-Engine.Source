namespace XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     Produces the per-conversation SEED that KEYS the untrusted-content fence nonce around attachment context. The seed
///     is derived from a SERVER-HELD secret (a purpose-derived subkey of the node key), NOT from the conversation id
///     alone. This matters because the conversation id is returned to clients: a client that knows it could otherwise
///     compute the exact closing marker of a conversation-id-seeded fence and forge a break-out. Deriving from a secret
///     the client never sees keeps the fence un-forgeable while remaining STABLE per conversation (so the attachment
///     prompt prefix does not change each turn and llama.cpp prompt/KV-cache prefix reuse is preserved).
///     <para>
///         The framing binds the marker to BOTH this seed and the fenced content (the seed keys an HMAC over the payload
///         — see <c>UntrustedContentFraming.WrapDocument(body, metadata, nonceSeed)</c>). A stable per-conversation seed
///         therefore still yields a byte-stable prefix for an unchanged attachment, while two DIFFERENT attachments in
///         the same conversation get different closing markers — closing the marker-replay gap where one attachment's
///         model-visible marker could otherwise be embedded in a later attachment to forge its fence close.
///     </para>
/// </summary>
public interface IUntrustedContentFenceSeedProvider
{
    /// <summary>
    ///     Returns a stable, high-entropy, client-unknowable seed string for <paramref name="conversationId" />, suitable
    ///     to pass as the <c>nonceSeed</c> of <c>UntrustedContentFraming.WrapDocument</c>. The same conversation always
    ///     yields the same seed within a node; a different node key (or conversation) yields a different seed.
    /// </summary>
    string DeriveSeed(Guid conversationId);
}
