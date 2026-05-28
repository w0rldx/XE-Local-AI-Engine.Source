namespace XE_Local_AI_Engine.Client.Services.Invocation.Envelope.Implementation;

using System.Security.Cryptography;
using System.Text;
using NSec.Cryptography;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;
using XE_Local_AI_Engine.Client.Models.Encrypted;

public sealed class EnvelopeCryptoService : IEnvelopeCryptoService
{
    private const int EpochKeyLength = 32;
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private static readonly byte[] NodeWrapInfo = Encoding.UTF8.GetBytes("c0re-node-wrap|v1");

    public EnvelopeDecryptionResult DecryptRuntimePackage(EncryptedRuntimePackageDto package, Key nodePrivateKey)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(nodePrivateKey);

        return DecryptEnvelope(package.ConversationId,
            package.MessageId,
            package.EpochVersion,
            package.Aad,
            package.ContentIv.Span,
            package.Ciphertext.Span,
            package.NodeWrappedEpochKey.Span,
            package.ClientEphemeralPublicKey.Span,
            nodePrivateKey,
            "Encrypted runtime package");
    }

    public EnvelopeDecryptionResult DecryptConversationMessage(Guid conversationId, EncryptedConversationMessageDto message, Key nodePrivateKey)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(nodePrivateKey);

        return DecryptEnvelope(conversationId,
            message.Id,
            message.EpochVersion,
            message.Aad,
            message.ContentIv.Span,
            message.Ciphertext.Span,
            message.NodeWrappedEpochKey.Span,
            message.ClientEphemeralPublicKey.Span,
            nodePrivateKey,
            "Encrypted conversation message");
    }

    public EncryptedChunkEnvelopeV1 EncryptChunk(Guid conversationId,
        Guid messageId,
        int epochVersion,
        ReadOnlySpan<byte> epochKey,
        ReadOnlySpan<byte> plaintext,
        long sequence,
        string kind = EncryptedChunkEnvelopeV1.ContentKind)
    {
        ValidateEncryptedFieldKind(kind);

        var (nonce, ciphertext) = EncryptPayload(plaintext, epochKey, BuildChunkAad(conversationId, messageId, epochVersion, sequence, kind));

        return new EncryptedChunkEnvelopeV1
        {
            ConversationId = conversationId,
            MessageId = messageId,
            EpochVersion = epochVersion,
            Kind = kind,
            ChunkIv = nonce,
            ChunkCiphertext = ciphertext,
            Sequence = sequence
        };
    }

    public EncryptedCompletedEnvelopeV1 EncryptCompleted(Guid conversationId,
        Guid messageId,
        int epochVersion,
        ReadOnlySpan<byte> epochKey,
        ReadOnlySpan<byte> plaintext,
        long totalSequence,
        IReadOnlyDictionary<string, long> tokenCounts,
        ReadOnlyMemory<byte>? reasoningPlaintext = null)
    {
        ArgumentNullException.ThrowIfNull(tokenCounts);

        var (nonce, ciphertext) = EncryptPayload(plaintext, epochKey, BuildEnvelopeAad(conversationId, messageId, epochVersion));
        (byte[] Nonce, byte[] Ciphertext)? reasoning = reasoningPlaintext is { } value
            ? EncryptPayload(value.Span, epochKey, BuildReasoningEnvelopeAad(conversationId, messageId, epochVersion))
            : null;

        return new EncryptedCompletedEnvelopeV1
        {
            ConversationId = conversationId,
            MessageId = messageId,
            EpochVersion = epochVersion,
            FinalIv = nonce,
            FinalCiphertext = ciphertext,
            ReasoningFinalIv = reasoning?.Nonce,
            ReasoningFinalCiphertext = reasoning?.Ciphertext,
            TotalSequence = totalSequence,
            TokenCounts = tokenCounts
        };
    }

    private static EnvelopeDecryptionResult DecryptEnvelope(Guid conversationId,
        Guid messageId,
        int epochVersion,
        string providedAad,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> ciphertext,
        ReadOnlySpan<byte> wrappedEpochKey,
        ReadOnlySpan<byte> clientEphemeralPublicKey,
        Key nodePrivateKey,
        string envelopeName)
    {
        var expectedAadString = BuildEnvelopeAadString(conversationId, messageId, epochVersion);

        if (!string.Equals(providedAad, expectedAadString, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{envelopeName} AAD did not match the expected envelope metadata.");
        }

        var expectedAad = Encoding.UTF8.GetBytes(expectedAadString);

        ValidateWrappedPayload(nonce, ciphertext, wrappedEpochKey, clientEphemeralPublicKey);

        var epochKey = UnwrapEpochKey(wrappedEpochKey, clientEphemeralPublicKey, nodePrivateKey);

        try
        {
            var plaintext = DecryptPayload(nonce, ciphertext, epochKey, expectedAad);
            return new EnvelopeDecryptionResult(plaintext, epochKey);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(epochKey);
            throw;
        }
    }

    private static void ValidateWrappedPayload(ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> ciphertext,
        ReadOnlySpan<byte> wrappedEpochKey,
        ReadOnlySpan<byte> clientEphemeralPublicKey)
    {
        if (nonce.Length != NonceLength)
        {
            throw new InvalidOperationException($"Envelope nonce must be {NonceLength} bytes.");
        }

        if (ciphertext.Length <= TagLength)
        {
            throw new InvalidOperationException("Envelope ciphertext must include at least one byte of plaintext plus the authentication tag.");
        }

        if (wrappedEpochKey.IsEmpty)
        {
            throw new InvalidOperationException("Wrapped epoch key is required.");
        }

        if (clientEphemeralPublicKey.IsEmpty)
        {
            throw new InvalidOperationException("Client ephemeral public key is required.");
        }
    }

    private static byte[] UnwrapEpochKey(ReadOnlySpan<byte> wrappedEpochKey, ReadOnlySpan<byte> clientEphemeralPublicKey, Key nodePrivateKey)
    {
        var algorithm = KeyAgreementAlgorithm.X25519;
        var publicKey = PublicKey.Import(algorithm, clientEphemeralPublicKey, KeyBlobFormat.RawPublicKey);
        var creationParameters = new SharedSecretCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport
        };

        using var sharedSecret = algorithm.Agree(nodePrivateKey, publicKey, creationParameters)
                                 ?? throw new CryptographicException("X25519 key agreement failed for the provided client ephemeral public key.");

        var sharedSecretBytes = sharedSecret.Export(SharedSecretBlobFormat.RawSharedSecret);
        var wrapKey = new byte[EpochKeyLength];

        try
        {
            HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecretBytes, wrapKey, ReadOnlySpan<byte>.Empty, NodeWrapInfo);
            return UnwrapKeyWithAesKw(wrappedEpochKey, wrapKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sharedSecretBytes);
            CryptographicOperations.ZeroMemory(wrapKey);
        }
    }

    private static byte[] UnwrapKeyWithAesKw(ReadOnlySpan<byte> wrappedEpochKey, ReadOnlySpan<byte> wrapKey)
    {
        IWrapper wrapEngine = new Rfc3394WrapEngine(new AesEngine());
        wrapEngine.Init(false, new KeyParameter(wrapKey.ToArray()));

        var epochKey = wrapEngine.Unwrap(wrappedEpochKey.ToArray(), 0, wrappedEpochKey.Length);
        if (epochKey.Length != EpochKeyLength)
        {
            CryptographicOperations.ZeroMemory(epochKey);
            throw new InvalidOperationException($"Unwrapped epoch key must be {EpochKeyLength} bytes.");
        }

        return epochKey;
    }

    private static (byte[] Nonce, byte[] Ciphertext) EncryptPayload(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> epochKey, byte[] aad)
    {
        ValidateEpochKey(epochKey);

        var nonce = new byte[NonceLength];
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagLength];
        var combined = new byte[ciphertext.Length + tag.Length];

        RandomNumberGenerator.Fill(nonce);

        using var aesGcm = new AesGcm(epochKey, TagLength);
        aesGcm.Encrypt(nonce, plaintext, ciphertext, tag, aad);

        ciphertext.CopyTo(combined, 0);
        tag.CopyTo(combined, ciphertext.Length);
        return (nonce, combined);
    }

    private static byte[] DecryptPayload(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertextWithTag, ReadOnlySpan<byte> epochKey, byte[] aad)
    {
        ValidateEpochKey(epochKey);

        var ciphertextLength = ciphertextWithTag.Length - TagLength;
        var plaintext = new byte[ciphertextLength];
        var ciphertext = ciphertextWithTag[..ciphertextLength];
        var tag = ciphertextWithTag[^TagLength..];

        using var aesGcm = new AesGcm(epochKey, TagLength);
        aesGcm.Decrypt(nonce, ciphertext, tag, plaintext, aad);
        return plaintext;
    }

    private static void ValidateEpochKey(ReadOnlySpan<byte> epochKey)
    {
        if (epochKey.Length != EpochKeyLength)
        {
            throw new InvalidOperationException($"Epoch key must be {EpochKeyLength} bytes.");
        }
    }

    private static byte[] BuildEnvelopeAad(Guid conversationId, Guid messageId, int epochVersion)
    {
        return Encoding.UTF8.GetBytes(BuildEnvelopeAadString(conversationId, messageId, epochVersion));
    }

    private static byte[] BuildReasoningEnvelopeAad(Guid conversationId, Guid messageId, int epochVersion)
    {
        return Encoding.UTF8.GetBytes($"{BuildEnvelopeAadString(conversationId, messageId, epochVersion)}|reasoning");
    }

    private static byte[] BuildChunkAad(Guid conversationId, Guid messageId, int epochVersion, long sequence, string kind)
    {
        var aad = string.Equals(kind, EncryptedChunkEnvelopeV1.ReasoningKind, StringComparison.Ordinal)
            ? $"chunk|{conversationId:D}|{messageId:D}|{epochVersion}|reasoning|{sequence}"
            : $"chunk|{conversationId:D}|{messageId:D}|{epochVersion}|{sequence}";

        return Encoding.UTF8.GetBytes(aad);
    }

    private static void ValidateEncryptedFieldKind(string kind)
    {
        if (string.Equals(kind, EncryptedChunkEnvelopeV1.ContentKind, StringComparison.Ordinal)
            || string.Equals(kind, EncryptedChunkEnvelopeV1.ReasoningKind, StringComparison.Ordinal))
        {
            return;
        }

        throw new ArgumentOutOfRangeException(nameof(kind), kind, "Encrypted chunk kind must be content or reasoning.");
    }

    private static string BuildEnvelopeAadString(Guid conversationId, Guid messageId, int epochVersion)
    {
        return $"message|{conversationId:D}|{messageId:D}|{epochVersion}";
    }
}
