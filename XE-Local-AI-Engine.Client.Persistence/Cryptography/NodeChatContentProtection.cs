namespace XE_Local_AI_Engine.Client.Persistence.Cryptography;

/// <summary>
///     Versioned at-rest envelope over <see cref="NodePayloadProtector" /> for the message <c>content</c> and
///     <c>metadata_json</c> columns. These two columns are the only encrypted columns with legacy <em>plaintext</em>
///     rows already on disk (they were written as raw UTF-8 by the raw-ADO persistence path before content encryption
///     shipped), so their reader must tell an encrypted blob apart from a legacy plaintext blob without guessing.
/// </summary>
/// <remarks>
///     <para>
///         The envelope prepends a two-byte header <c>0xFE 0x01</c> to the <see cref="NodePayloadProtector" /> payload
///         (<c>nonce || ciphertext || tag</c>). <c>0xFE</c> is never a valid UTF-8 lead byte, so it can never begin a
///         legacy plaintext blob (message content and <c>metadata_json</c> are always produced from a .NET string via
///         <c>Encoding.UTF8.GetBytes</c>) — the header therefore cannot collide with any plaintext start. The second
///         byte is a format version so the framing can evolve.
///     </para>
///     <para>
///         Reads are <em>read-both</em>: a blob carrying the header is decrypted; a blob without it is a legacy
///         plaintext row and is returned verbatim. A table can therefore be migrated incrementally and stay fully
///         readable throughout. The inner ciphertext uses the identical primitive and AAD
///         (<c>conversationId + messageId + column</c>) as every other encrypted column, so the header is a pure
///         prefix — an existing envelope-wrapped row is byte-compatible with <see cref="NodePayloadProtector" /> once
///         the header is stripped.
///     </para>
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
    ///     Recovers the plaintext bytes from <paramref name="stored" />. An enveloped blob is authenticated-decrypted; a
    ///     legacy plaintext blob (no header) is returned as a copy. This is the single read-both path shared by the raw
    ///     persistence path, the EF materialization interceptor, and the content-encryption migration.
    /// </summary>
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
