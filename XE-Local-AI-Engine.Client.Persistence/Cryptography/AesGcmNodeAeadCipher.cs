namespace XE_Local_AI_Engine.Client.Persistence.Cryptography;

using System.Security.Cryptography;

/// <summary>
///     AES-256-GCM implementation of <see cref="INodeAeadCipher" /> with a 12-byte nonce and a 16-byte tag. The single
///     owner of <see cref="AesGcm" /> construction and the tag-size constant for the node — both the at-rest column
///     protector and the streaming envelope crypto delegate their authenticated transform here.
/// </summary>
public sealed class AesGcmNodeAeadCipher : INodeAeadCipher
{
    private const int NonceLength = 12;
    private const int TagLength = 16;

    /// <inheritdoc />
    public int NonceSize => NonceLength;

    /// <inheritdoc />
    public int TagSize => TagLength;

    /// <inheritdoc />
    public AeadCiphertext Encrypt(ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> associatedData)
    {
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagLength];

        using var aesGcm = new AesGcm(key, TagLength);
        aesGcm.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);

        return new AeadCiphertext(ciphertext, tag);
    }

    /// <inheritdoc />
    public byte[] Decrypt(ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> ciphertext,
        ReadOnlySpan<byte> tag,
        ReadOnlySpan<byte> associatedData)
    {
        var plaintext = new byte[ciphertext.Length];

        using var aesGcm = new AesGcm(key, TagLength);
        aesGcm.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);

        return plaintext;
    }
}
