namespace XE_Local_AI_Engine.Client.Services.Invocation.Envelope;

using System.Security.Cryptography;

/// <summary>
///     Represents envelope decryption result.
/// </summary>
public sealed class EnvelopeDecryptionResult : IDisposable
{
    private byte[]? _epochKey;
    private byte[]? _plaintext;

    public EnvelopeDecryptionResult(byte[] plaintext, byte[] epochKey)
    {
        _plaintext = plaintext ?? throw new ArgumentNullException(nameof(plaintext));
        _epochKey = epochKey ?? throw new ArgumentNullException(nameof(epochKey));
    }

    public ReadOnlyMemory<byte> Plaintext =>
        _plaintext is not null
            ? _plaintext
            : throw new ObjectDisposedException(nameof(EnvelopeDecryptionResult));

    public ReadOnlyMemory<byte> EpochKey =>
        _epochKey is not null
            ? _epochKey
            : throw new ObjectDisposedException(nameof(EnvelopeDecryptionResult));

    public void Dispose()
    {
        if (_plaintext is not null)
        {
            CryptographicOperations.ZeroMemory(_plaintext);
            _plaintext = null;
        }

        if (_epochKey is not null)
        {
            CryptographicOperations.ZeroMemory(_epochKey);
            _epochKey = null;
        }
    }
}
