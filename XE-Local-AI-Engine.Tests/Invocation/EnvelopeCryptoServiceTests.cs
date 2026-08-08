namespace XE_Local_AI_Engine.Tests.Invocation;

using System.Security.Cryptography;
using System.Text;
using XE_Local_AI_Engine.Client.Models.Encrypted;
using XE_Local_AI_Engine.Client.Persistence.Cryptography;
using XE_Local_AI_Engine.Client.Services.Invocation.Envelope.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class EnvelopeCryptoServiceTests
{
    [Test]
    public void EncryptChunk_WhenContentKind_UsesC0reContentChunkAad()
    {
        var service = new EnvelopeCryptoService(new AesGcmNodeAeadCipher());
        var conversationId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var epochKey = CreateEpochKey();

        var encrypted = service.EncryptChunk(conversationId, messageId, epochVersion: 3, epochKey, Encoding.UTF8.GetBytes("hello"), sequence: 7);

        var plaintext = Decrypt(encrypted.ChunkIv.Span,
            encrypted.ChunkCiphertext.Span,
            epochKey,
            $"chunk|{conversationId:D}|{messageId:D}|3|7");

        AssertEx.Equal("hello", Encoding.UTF8.GetString(plaintext));
        AssertEx.Equal(EncryptedChunkEnvelopeV1.ContentKind, encrypted.Kind);
    }

    [Test]
    public void EncryptChunk_WhenReasoningKind_UsesC0reReasoningChunkAad()
    {
        var service = new EnvelopeCryptoService(new AesGcmNodeAeadCipher());
        var conversationId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var epochKey = CreateEpochKey();

        var encrypted = service.EncryptChunk(conversationId,
            messageId,
            epochVersion: 3,
            epochKey,
            Encoding.UTF8.GetBytes("thinking"),
            sequence: 2,
            EncryptedChunkEnvelopeV1.ReasoningKind);

        var plaintext = Decrypt(encrypted.ChunkIv.Span,
            encrypted.ChunkCiphertext.Span,
            epochKey,
            $"chunk|{conversationId:D}|{messageId:D}|3|reasoning|2");

        AssertEx.Equal("thinking", Encoding.UTF8.GetString(plaintext));
        AssertEx.Equal(EncryptedChunkEnvelopeV1.ReasoningKind, encrypted.Kind);
    }

    [Test]
    public void EncryptCompleted_WhenReasoningProvided_UsesC0reMessageAads()
    {
        var service = new EnvelopeCryptoService(new AesGcmNodeAeadCipher());
        var conversationId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var epochKey = CreateEpochKey();

        var encrypted = service.EncryptCompleted(conversationId,
            messageId,
            epochVersion: 4,
            epochKey,
            Encoding.UTF8.GetBytes("answer"),
            totalSequence: 1,
            new Dictionary<string, long>
            {
                ["tokensUsed"] = 2
            },
            Encoding.UTF8.GetBytes("final thinking"));

        var contentPlaintext = Decrypt(encrypted.FinalIv.Span,
            encrypted.FinalCiphertext.Span,
            epochKey,
            $"message|{conversationId:D}|{messageId:D}|4");
        var reasoningPlaintext = Decrypt(encrypted.ReasoningFinalIv!.Value.Span,
            encrypted.ReasoningFinalCiphertext!.Value.Span,
            epochKey,
            $"message|{conversationId:D}|{messageId:D}|4|reasoning");

        AssertEx.Equal("answer", Encoding.UTF8.GetString(contentPlaintext));
        AssertEx.Equal("final thinking", Encoding.UTF8.GetString(reasoningPlaintext));
    }

    private static byte[] CreateEpochKey()
    {
        return Enumerable.Range(start: 0, count: 32).Select(value => (byte)value).ToArray();
    }

    private static byte[] Decrypt(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertextWithTag, ReadOnlySpan<byte> epochKey, string aad)
    {
        var ciphertextLength = ciphertextWithTag.Length - 16;
        var plaintext = new byte[ciphertextLength];
        var ciphertext = ciphertextWithTag[..ciphertextLength];
        var tag = ciphertextWithTag[^16..];

        using var aesGcm = new AesGcm(epochKey, tagSizeInBytes: 16);
        aesGcm.Decrypt(nonce, ciphertext, tag, plaintext, Encoding.UTF8.GetBytes(aad));
        return plaintext;
    }
}
