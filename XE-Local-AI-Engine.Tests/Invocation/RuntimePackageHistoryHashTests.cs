namespace XE_Local_AI_Engine.Tests.Invocation;

using XE_Local_AI_Engine.Client.Models.Encrypted;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Services.Invocation.RuntimePackage;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class RuntimePackageHistoryHashTests
{
    [Test]
    public void Compute_WhenHistoryIsEmpty_ReturnsSharedVectorDigest()
    {
        var digest = RuntimePackageHistoryHash.Compute([]);

        AssertEx.Equal("4f53cda18c2baa0c0354bb5f9a3ecbe5ed12ab4d8e11ba873c2f11161202b945", digest);
    }

    [Test]
    public void BuildExpectedAad_WhenCurrentMessageInputsProvided_ReturnsSharedVector()
    {
        var actual = RuntimePackageHistoryHash.BuildExpectedAad(Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            7);

        AssertEx.Equal("message|22222222-2222-2222-2222-222222222222|11111111-1111-1111-1111-111111111111|7", actual);
    }

    [Test]
    public void BuildExpectedAad_WhenHistoryMessageInputsProvided_ReturnsSharedVector()
    {
        var actual = RuntimePackageHistoryHash.BuildExpectedAad(Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            9);

        AssertEx.Equal("message|22222222-2222-2222-2222-222222222222|33333333-3333-3333-3333-333333333333|9", actual);
    }

    [Test]
    public void Compute_WhenSortOrderTies_OrdersByGuidString()
    {
        var first = new EncryptedConversationMessageDto
        {
            Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Role = MessageRole.User,
            SortOrder = 10,
            EpochVersion = 7,
            Aad = "message|conversation|11111111-1111-1111-1111-111111111111|7",
            NodeWrappedEpochKey = new byte[]
            {
                1
            },
            ClientEphemeralPublicKey = new byte[]
            {
                2
            },
            Ciphertext = new byte[]
            {
                3
            },
            ContentIv = new byte[]
            {
                4
            }
        };

        var second = first with
        {
            Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Aad = "message|conversation|22222222-2222-2222-2222-222222222222|7"
        };

        // Both orderings must produce the same hash (canonical sort is deterministic)
        var h1 = RuntimePackageHistoryHash.Compute([second, first]);
        var h2 = RuntimePackageHistoryHash.Compute([first, second]);

        AssertEx.Equal(h1, h2);
    }
}
