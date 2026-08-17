namespace XE_Local_AI_Engine.Client.Persistence.Cryptography;

/// <summary>
///     The two halves an AEAD encryption produces: the ciphertext (same length as the plaintext) and the separate
///     authentication tag. The caller arranges the on-the-wire layout, so they stay separate here.
/// </summary>
public sealed record AeadCiphertext(byte[] Ciphertext, byte[] Tag);
