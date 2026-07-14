namespace XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     Produces the per-conversation SEED used to nonce the untrusted-content fence around attachment context. The seed
///     is derived from a SERVER-HELD secret (a purpose-derived subkey of the node key), NOT from the conversation id
///     alone. This matters because the conversation id is returned to clients: a client that knows it could otherwise
///     compute the exact closing marker of a conversation-id-seeded fence and forge a break-out. Deriving from a secret
///     the client never sees keeps the fence un-forgeable while remaining STABLE per conversation (so the attachment
///     prompt prefix does not change each turn and llama.cpp prompt/KV-cache prefix reuse is preserved).
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
