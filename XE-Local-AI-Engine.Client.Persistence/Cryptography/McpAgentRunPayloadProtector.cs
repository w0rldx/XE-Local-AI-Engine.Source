namespace XE_Local_AI_Engine.Client.Persistence.Cryptography;

using System.Security.Cryptography;
using System.Text;

/// <summary>Domain-separated encryption and request-identity protection for inbound MCP runs.</summary>
public sealed class McpAgentRunPayloadProtector : IDisposable
{
    public const int FixedRecordOverheadBytes = 256;

    private static readonly byte[] EmptySalt = [];
    private readonly INodeAeadCipher _cipher;
    private byte[]? _payloadKey;
    private byte[]? _fingerprintKey;

    public McpAgentRunPayloadProtector(INodeSqliteKeyHolder keyHolder, INodeAeadCipher cipher)
    {
        ArgumentNullException.ThrowIfNull(keyHolder);
        _cipher = cipher ?? throw new ArgumentNullException(nameof(cipher));
        _payloadKey = DeriveKey(keyHolder.Key.Span, "c0re-node-mcp-agent-run-payload|v1");
        _fingerprintKey = DeriveKey(keyHolder.Key.Span, "c0re-node-mcp-agent-run-fingerprint|v1");
    }

    public int FixedEnvelopeOverheadBytes => _cipher.NonceSize + _cipher.TagSize;

    public byte[] ComputeRequestFingerprint(ReadOnlySpan<byte> canonicalRequest)
    {
        ObjectDisposedException.ThrowIf(_fingerprintKey is null, this);
        return HMACSHA256.HashData(_fingerprintKey, canonicalRequest);
    }

    public byte[] Protect(Guid requestId, string fieldName, ReadOnlySpan<byte> plaintext)
    {
        ObjectDisposedException.ThrowIf(_payloadKey is null, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);

        var nonce = RandomNumberGenerator.GetBytes(_cipher.NonceSize);
        var aad = BuildAssociatedData(requestId, fieldName);
        var (ciphertext, tag) = _cipher.Encrypt(_payloadKey, nonce, plaintext, aad);
        var envelope = new byte[nonce.Length + ciphertext.Length + tag.Length];
        nonce.CopyTo(envelope, index: 0);
        ciphertext.CopyTo(envelope, nonce.Length);
        tag.CopyTo(envelope, nonce.Length + ciphertext.Length);
        return envelope;
    }

    public byte[] Unprotect(Guid requestId, string fieldName, ReadOnlySpan<byte> envelope)
    {
        ObjectDisposedException.ThrowIf(_payloadKey is null, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);
        if (envelope.Length < FixedEnvelopeOverheadBytes)
        {
            throw new InvalidOperationException("The encrypted MCP run payload is truncated.");
        }

        var ciphertextLength = envelope.Length - FixedEnvelopeOverheadBytes;
        return _cipher.Decrypt(_payloadKey,
            envelope[.._cipher.NonceSize],
            envelope.Slice(_cipher.NonceSize, ciphertextLength),
            envelope[^_cipher.TagSize..],
            BuildAssociatedData(requestId, fieldName));
    }

    public void Dispose()
    {
        if (_payloadKey is not null)
        {
            CryptographicOperations.ZeroMemory(_payloadKey);
            _payloadKey = null;
        }

        if (_fingerprintKey is not null)
        {
            CryptographicOperations.ZeroMemory(_fingerprintKey);
            _fingerprintKey = null;
        }
    }

    private static byte[] DeriveKey(ReadOnlySpan<byte> nodeKey, string infoText)
    {
        var key = new byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, nodeKey, key, EmptySalt, Encoding.UTF8.GetBytes(infoText));
        return key;
    }

    private static byte[] BuildAssociatedData(Guid requestId, string fieldName)
    {
        return Encoding.UTF8.GetBytes($"mcp-agent-run|v1|{requestId:D}|{fieldName}");
    }
}
