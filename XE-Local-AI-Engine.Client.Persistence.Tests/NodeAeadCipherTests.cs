namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using System.Security.Cryptography;
using System.Text;
using XE_Local_AI_Engine.Client.Persistence.Cryptography;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

public sealed class NodeAeadCipherTests
{
    [Test]
    public void Encrypt_ThenDecrypt_RecoversPlaintext()
    {
        var cipher = new AesGcmNodeAeadCipher();
        var key = CreateKey();
        var nonce = CreateNonce(cipher);
        var plaintext = Encoding.UTF8.GetBytes("node-aead-roundtrip");
        var aad = Encoding.UTF8.GetBytes("content|v1");

        var (ciphertext, tag) = cipher.Encrypt(key, nonce, plaintext, aad);
        var recovered = cipher.Decrypt(key, nonce, ciphertext, tag, aad);

        AssertEx.True(tag.Length == cipher.TagSize, "Tag length should equal TagSize.");
        AssertEx.True(ciphertext.Length == plaintext.Length, "Ciphertext length should equal plaintext length (GCM).");
        AssertEx.True(plaintext.SequenceEqual(recovered), "Decrypt should recover the original plaintext.");
    }

    [Test]
    public void Decrypt_WhenTagTampered_Throws()
    {
        var cipher = new AesGcmNodeAeadCipher();
        var key = CreateKey();
        var nonce = CreateNonce(cipher);
        var aad = Encoding.UTF8.GetBytes("content|v1");
        var (ciphertext, tag) = cipher.Encrypt(key, nonce, Encoding.UTF8.GetBytes("secret"), aad);
        tag[0] ^= 0xFF;

        _ = AssertEx.Throws<AuthenticationTagMismatchException>(() => cipher.Decrypt(key, nonce, ciphertext, tag, aad));
    }

    [Test]
    public void Decrypt_WhenAssociatedDataMismatched_Throws()
    {
        var cipher = new AesGcmNodeAeadCipher();
        var key = CreateKey();
        var nonce = CreateNonce(cipher);
        var (ciphertext, tag) = cipher.Encrypt(key, nonce, Encoding.UTF8.GetBytes("secret"), Encoding.UTF8.GetBytes("content|v1"));

        _ = AssertEx.Throws<AuthenticationTagMismatchException>(() => cipher.Decrypt(key, nonce, ciphertext, tag, Encoding.UTF8.GetBytes("content|v2")));
    }

    [Test]
    public void Sizes_AreAes256GcmDefaults()
    {
        var cipher = new AesGcmNodeAeadCipher();

        AssertEx.True(cipher.NonceSize == 12, "AES-GCM nonce should be 12 bytes.");
        AssertEx.True(cipher.TagSize == 16, "AES-GCM tag should be 16 bytes.");
    }

    private static byte[] CreateKey()
    {
        return Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray();
    }

    private static byte[] CreateNonce(INodeAeadCipher cipher)
    {
        var nonce = new byte[cipher.NonceSize];
        RandomNumberGenerator.Fill(nonce);
        return nonce;
    }
}
