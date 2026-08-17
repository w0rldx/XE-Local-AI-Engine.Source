namespace XE_Local_AI_Engine.Client.Persistence.Cryptography;

using System.Diagnostics.CodeAnalysis;

/// <summary>
///     The two halves an AEAD encryption produces: the ciphertext (same length as the plaintext) and the separate
///     authentication tag. The caller arranges the on-the-wire layout, so they stay separate here.
/// </summary>
[SuppressMessage("Performance", "CA1819:Properties should not return arrays",
    Justification =
        "Both arrays are allocated fresh by the encrypt call and handed to the caller, which owns them and writes them straight into its own wire layout. Wrapping them in ReadOnlyMemory would add a copy on every at-rest column write and every envelope frame.")]
public sealed record AeadCiphertext(byte[] Ciphertext, byte[] Tag);
