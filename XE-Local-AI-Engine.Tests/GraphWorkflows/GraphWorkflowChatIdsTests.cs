namespace XE_Local_AI_Engine.Tests.GraphWorkflows;

using XE_Local_AI_Engine.Client.Services.GraphWorkflows.Chat;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>The version 5 UUIDs the chat binding writes, pinned against outputs of an independent RFC 4122 implementation.</summary>
[Category(TestCategories.Unit)]
public sealed class GraphWorkflowChatIdsTests
{
    [Test]
    [Arguments("6ba7b810-9dad-11d1-80b4-00c04fd430c8", "python.org", "886313e1-3b8a-5372-9b90-0c9aee199e5d")]
    [Arguments("6ba7b811-9dad-11d1-80b4-00c04fd430c8", "https://example.com/", "dd2c1780-811a-5296-81c5-178a0ef488bc")]
    public void NameBased_MatchesTheRfcAlgorithm(string namespaceId, string name, string expected) =>
        AssertEx.Equal(Guid.Parse(expected), GraphWorkflowChatIds.NameBased(Guid.Parse(namespaceId), name));

    [Test]
    public void TheMessageIds_AreDeterministicInTheirInputs()
    {
        AssertEx.Equal(Guid.Parse("f08eb9ad-d90c-5ef5-9447-28507de0bc4e"), GraphWorkflowChatIds.UserMessage(Guid.Parse("00000000-0000-0000-0000-000000000001")));
        AssertEx.Equal(Guid.Parse("74b14847-1bd6-5a18-9a6b-2187d5f75758"),
            GraphWorkflowChatIds.PublishedMessage(Guid.Parse("00000000-0000-0000-0000-000000000002"), "done", attempt: 1));
        AssertEx.NotEqual(GraphWorkflowChatIds.PublishedMessage(Guid.Empty, "done", attempt: 1), GraphWorkflowChatIds.PublishedMessage(Guid.Empty, "done", attempt: 2),
            "each attempt publishes its own message.");
    }
}
