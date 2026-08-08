namespace XE_Local_AI_Engine.Client.Persistence.Cryptography;

/// <summary>
///     The single AES-GCM AEAD primitive for the node. Owns the nonce/tag sizing and the underlying
///     <see cref="System.Security.Cryptography.AesGcm" /> calls so the at-rest column protector and the streaming
///     envelope crypto share one implementation instead of each hand-rolling <c>new AesGcm(...)</c>.
/// </summary>
/// <remarks>
///     The caller supplies the nonce (each consumer owns its own random-nonce generation and on-the-wire layout); this
///     contract is only the authenticated encrypt/decrypt transform. Implementations are stateless and thread-safe.
/// </remarks>
public interface INodeAeadCipher
{
    /// <summary>Required nonce length in bytes.</summary>
    int NonceSize { get; }

    /// <summary>Authentication tag length in bytes.</summary>
    int TagSize { get; }

    /// <summary>
    ///     Encrypts <paramref name="plaintext" /> under <paramref name="key" /> and <paramref name="nonce" />, binding
    ///     <paramref name="associatedData" />. Returns the ciphertext (same length as the plaintext) and the
    ///     <see cref="TagSize" />-byte authentication tag separately; the caller arranges the on-the-wire layout.
    /// </summary>
    (byte[] Ciphertext, byte[] Tag) Encrypt(ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> associatedData);

    /// <summary>
    ///     Verifies <paramref name="tag" /> and decrypts <paramref name="ciphertext" /> under <paramref name="key" />
    ///     and <paramref name="nonce" />, binding <paramref name="associatedData" />. Throws
    ///     <see cref="System.Security.Cryptography.AuthenticationTagMismatchException" /> when authentication fails.
    /// </summary>
    byte[] Decrypt(ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> ciphertext,
        ReadOnlySpan<byte> tag,
        ReadOnlySpan<byte> associatedData);
}
