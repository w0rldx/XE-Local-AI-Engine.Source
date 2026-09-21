namespace XE_Local_AI_Engine.Client.Persistence.Cryptography;

/// <summary>
///     Versioned at-rest envelope over <see cref="NodePayloadProtector" /> for the message <c>content</c> and
///     <c>metadata_json</c> columns, the only encrypted columns with legacy plaintext rows already on disk.
/// </summary>
/// <remarks>
///     A two-byte header <c>0xFE 0x01</c> (marker, format version) prefixes the <c>nonce || ciphertext || tag</c>
///     payload; <c>0xFE</c> is never a valid UTF-8 lead byte, so it cannot begin a legacy plaintext blob. Reads are
///     read-both — a headed blob is decrypted, a headless one returned verbatim — so a table migrates incrementally
///     and stays readable. The inner AAD is <c>conversationId + messageId + column</c> as elsewhere, making the
///     header a pure prefix. See docs/wiki/08-data-and-persistence.md ("The content envelope").
/// </remarks>
internal static class NodeChatContentProtection
{
    private const byte EnvelopeMarker = 0xFE;
    private const byte EnvelopeVersion = 0x01;
    private const int HeaderLength = 2;

    /// <summary>
    ///     Returns whether <paramref name="stored" /> carries the encrypted-envelope header. A false result means the
    ///     blob is legacy plaintext (or empty).
    /// </summary>
    public static bool IsProtected(ReadOnlySpan<byte> stored)
    {
        return stored.Length >= HeaderLength && stored[0] == EnvelopeMarker && stored[1] == EnvelopeVersion;
    }

    /// <summary>
    ///     Encrypts <paramref name="plaintext" /> and wraps it in the versioned envelope. The AAD binds
    ///     <paramref name="conversationId" />, <paramref name="recordId" /> and <paramref name="columnName" /> exactly as
    ///     <see cref="NodePayloadProtector" /> does.
    /// </summary>
    public static byte[] Protect(ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> key,
        Guid conversationId,
        Guid recordId,
        string columnName)
    {
        var inner = NodePayloadProtector.Encrypt(plaintext, key, conversationId, recordId, columnName);

        var payload = new byte[HeaderLength + inner.Length];
        payload[0] = EnvelopeMarker;
        payload[1] = EnvelopeVersion;
        inner.CopyTo(payload, HeaderLength);
        return payload;
    }

    /// <summary>
    ///     Recovers the plaintext bytes from <paramref name="stored" />: an enveloped blob is authenticated-decrypted,
    ///     a legacy plaintext blob (no header) is returned as a copy.
    /// </summary>
    /// <remarks>
    ///     The single read-both path, shared by the raw persistence path, the EF materialization interceptor and the
    ///     content-encryption migration.
    /// </remarks>
    public static byte[] Unprotect(ReadOnlySpan<byte> stored,
        ReadOnlySpan<byte> key,
        Guid conversationId,
        Guid recordId,
        string columnName)
    {
        if (!IsProtected(stored))
        {
            // Legacy plaintext row written before content encryption shipped — the bytes are already the plaintext.
            return stored.ToArray();
        }

        return NodePayloadProtector.Decrypt(stored[HeaderLength..], key, conversationId, recordId, columnName);
    }
}
