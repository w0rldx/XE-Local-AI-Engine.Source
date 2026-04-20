namespace XE_Local_AI_Engine.Tests.Invocation;

using XE_Local_AI_Engine.Client.Models.Encrypted;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Services.Invocation.RuntimeEnvelope;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class RuntimePackageHistoryHashTests
{
    [Test]
    public void Compute_WhenHistoryIsEmpty_ReturnsSharedVectorDigest()
    {
        var canonicalJson = RuntimePackageHistoryHash.SerializeCanonicalJson([]);
        var digest = RuntimePackageHistoryHash.Compute([]);

        AssertEx.Equal("[]", canonicalJson);
        AssertEx.Equal("4f53cda18c2baa0c0354bb5f9a3ecbe5ed12ab4d8e11ba873c2f11161202b945", digest);
    }

    [Test]
    public void Compute_WhenUsingSharedSingleEntryVector_ReturnsExpectedDigest()
    {
        var canonicalJson = RuntimePackageHistoryHash.SerializeCanonicalJson([CreateVectorEntry()]);
        var digest = RuntimePackageHistoryHash.Compute([CreateVectorEntry()]);

        AssertEx.Equal("[{\"id\":\"11111111-1111-1111-1111-111111111111\",\"role\":\"user\",\"sortOrder\":10,\"epochVersion\":7,\"aad\":\"message|22222222-2222-2222-2222-222222222222|11111111-1111-1111-1111-111111111111|7\",\"nodeWrappedEpochKey\":\"AAECAwQFBgc=\",\"clientEphemeralPublicKey\":\"CAkKCwwNDg8=\",\"ciphertext\":\"EBESExQVFhc=\",\"contentIv\":\"GBkaGxwdHh8=\"}]",
            canonicalJson);
        AssertEx.Equal("48b074b9ff9a22175eea9299d1df91c8392ff1e4fbfd74750ce08c22e6d54043", digest);
    }

    [Test]
    public void SerializeCanonicalJson_WhenSortOrderTies_OrdersByGuidString()
    {
        var second = new EncryptedConversationMessageDto
        {
            Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Role = MessageRole.Assistant,
            SortOrder = 10,
            EpochVersion = 7,
            Aad = "message|conversation|22222222-2222-2222-2222-222222222222|7",
            NodeWrappedEpochKey = new byte[] { 1 },
            ClientEphemeralPublicKey = new byte[] { 2 },
            Ciphertext = new byte[] { 3 },
            ContentIv = new byte[] { 4 }
        };

        var first = second with
        {
            Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Aad = "message|conversation|11111111-1111-1111-1111-111111111111|7"
        };

        var canonicalJson = RuntimePackageHistoryHash.SerializeCanonicalJson([second, first]);

        AssertEx.True(canonicalJson.IndexOf(first.Id.ToString("D"), StringComparison.Ordinal) <
                      canonicalJson.IndexOf(second.Id.ToString("D"), StringComparison.Ordinal));
    }

    [Test]
    public void BuildExpectedAad_WhenCurrentMessageInputsProvided_ReturnsSharedVector()
    {
        var actual = RuntimePackageHistoryHash.BuildExpectedAad(
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            7);

        AssertEx.Equal("message|22222222-2222-2222-2222-222222222222|11111111-1111-1111-1111-111111111111|7", actual);
    }

    [Test]
    public void BuildExpectedAad_WhenHistoryMessageInputsProvided_ReturnsSharedVector()
    {
        var actual = RuntimePackageHistoryHash.BuildExpectedAad(
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            9);

        AssertEx.Equal("message|22222222-2222-2222-2222-222222222222|33333333-3333-3333-3333-333333333333|9", actual);
    }

    private static EncryptedConversationMessageDto CreateVectorEntry()
    {
        return new EncryptedConversationMessageDto
        {
            Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Role = MessageRole.User,
            SortOrder = 10,
            EpochVersion = 7,
            Aad = "message|22222222-2222-2222-2222-222222222222|11111111-1111-1111-1111-111111111111|7",
            NodeWrappedEpochKey = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7 },
            ClientEphemeralPublicKey = new byte[] { 8, 9, 10, 11, 12, 13, 14, 15 },
            Ciphertext = new byte[] { 16, 17, 18, 19, 20, 21, 22, 23 },
            ContentIv = new byte[] { 24, 25, 26, 27, 28, 29, 30, 31 }
        };
    }
}
