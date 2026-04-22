namespace XE_Local_AI_Engine.Tests.Invocation;

using XE_Local_AI_Engine.Client.Models.Encrypted;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Services.Invocation.RuntimeEnvelope;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Golden-vector parity tests for canonical hash. Hashes must match C0re server exactly.
///     Fixture source: tests/Fixtures/envelope-hash-v2/
/// </summary>
public sealed class RuntimePackageHistoryHashV2ParityTests
{
    private const string Aad = "YWFk";
    private static readonly Guid Id1 = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Id2 = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid IdA = new("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid IdB = new("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private static readonly byte[] Ct = Convert.FromBase64String("Y2lwaGVydGV4dA==");
    private static readonly byte[] Iv = Convert.FromBase64String("aXY=");

    [Test]
    public void Hash_EmptyList_MatchesC0reGoldenVector()
    {
        var hash = RuntimePackageHistoryHash.Compute([]);
        AssertEx.Equal("4f53cda18c2baa0c0354bb5f9a3ecbe5ed12ab4d8e11ba873c2f11161202b945", hash);
    }

    [Test]
    public void Hash_OneMessageAllFields_MatchesC0reGoldenVector()
    {
        var messages = new[]
        {
            MakeMsg(Id1, 1, MessageRole.User, 3, Ct, Iv, Aad)
        };
        var hash = RuntimePackageHistoryHash.Compute(messages);
        AssertEx.Equal("590c171df8f08be690bef534a1f224a66df4c4c53ecd7e357dea5755271ee9fc", hash);
    }

    [Test]
    public void Hash_EpochVersionZero_MatchesC0reGoldenVector()
    {
        var messages = new[]
        {
            MakeMsg(Id2, 0, MessageRole.Assistant, 0, Ct, Iv, Aad)
        };
        var hash = RuntimePackageHistoryHash.Compute(messages);
        AssertEx.Equal("bea716b80f646f05de320d61207bda21df1710a0403c6ac46687ecb9a7064c21", hash);
    }

    [Test]
    public void Hash_TwoMessages_OrderIndependent_MatchesC0reGoldenVector()
    {
        var ctA = Convert.FromBase64String("Yw==");
        var ctB = Convert.FromBase64String("Zg==");
        var ivSmall = Convert.FromBase64String("aQ==");
        const string aadSmall = "YQ==";

        var inOrder = new[]
        {
            MakeMsg(IdA, 1, MessageRole.User, 1, ctA, ivSmall, aadSmall),
            MakeMsg(IdB, 2, MessageRole.Assistant, 1, ctB, ivSmall, aadSmall)
        };
        var reversed = new[]
        {
            MakeMsg(IdB, 2, MessageRole.Assistant, 1, ctB, ivSmall, aadSmall),
            MakeMsg(IdA, 1, MessageRole.User, 1, ctA, ivSmall, aadSmall)
        };

        var h1 = RuntimePackageHistoryHash.Compute(inOrder);
        var h2 = RuntimePackageHistoryHash.Compute(reversed);

        AssertEx.Equal("672905ce8086793ad24582ba5d35dfd17f06d0e8b986d01998763979e0cbbcc1", h1);
        AssertEx.Equal(h1, h2);
    }

    [Test]
    public void Hash_RoleSystem_MatchesC0reGoldenVector()
    {
        var hash = RuntimePackageHistoryHash.Compute([MakeMsg(Id1, 1, MessageRole.System, 1, Ct, Iv, Aad)]);
        AssertEx.Equal("64a1ae0b8e0184eed328da1c59883b2d4acda7d2e3f310e27635fd08f59ea34e", hash);
    }

    [Test]
    public void Hash_RoleAssistant_MatchesC0reGoldenVector()
    {
        var hash = RuntimePackageHistoryHash.Compute([MakeMsg(Id1, 1, MessageRole.Assistant, 1, Ct, Iv, Aad)]);
        AssertEx.Equal("e46b44affd75a45fcd89383f0ef2f5d6d64c0a9f6b7721ea1281c74e2773f6eb", hash);
    }

    [Test]
    public void Hash_RoleTool_MatchesC0reGoldenVector()
    {
        var hash = RuntimePackageHistoryHash.Compute([MakeMsg(Id1, 1, MessageRole.Tool, 1, Ct, Iv, Aad)]);
        AssertEx.Equal("d7646bed0dea9c0259d0b2750194b22af4f5e41ed77335f001056e4684f55310", hash);
    }

    private static EncryptedConversationMessageDto MakeMsg(Guid id, int sortOrder, MessageRole role, int epochVersion,
        byte[] ciphertext, byte[] contentIv, string aad)
    {
        return new EncryptedConversationMessageDto
        {
            Id = id,
            Role = role,
            SortOrder = sortOrder,
            EpochVersion = epochVersion,
            Ciphertext = ciphertext,
            ContentIv = contentIv,
            Aad = aad,
            NodeWrappedEpochKey = Array.Empty<byte>(),
            ClientEphemeralPublicKey = Array.Empty<byte>()
        };
    }
}
