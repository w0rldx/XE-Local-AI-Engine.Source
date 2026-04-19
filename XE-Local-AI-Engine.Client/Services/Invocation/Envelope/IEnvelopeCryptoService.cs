namespace XE_Local_AI_Engine.Client.Services.Invocation.Envelope;

using NSec.Cryptography;
using XE_Local_AI_Engine.Client.Models.Encrypted;

public interface IEnvelopeCryptoService
{
    EnvelopeDecryptionResult DecryptRuntimePackage(EncryptedRuntimePackageDto package, Key nodePrivateKey);

    EncryptedChunkEnvelopeV1 EncryptChunk(Guid conversationId,
        Guid messageId,
        int epochVersion,
        ReadOnlySpan<byte> epochKey,
        ReadOnlySpan<byte> plaintext,
        long sequence);

    EncryptedCompletedEnvelopeV1 EncryptCompleted(Guid conversationId,
        Guid messageId,
        int epochVersion,
        ReadOnlySpan<byte> epochKey,
        ReadOnlySpan<byte> plaintext,
        long totalSequence,
        IReadOnlyDictionary<string, long> tokenCounts);
}
