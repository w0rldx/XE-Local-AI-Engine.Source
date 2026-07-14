namespace XE_Local_AI_Engine.Tests.Chat;

using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Pins the authoritative transition table so the allowed-source sets cannot drift from the atomic SQL predicates
///     that enforce them (cancel / flush / terminalize in the persistence commands, and restart recovery).
/// </summary>
public sealed class NodeChatMessageTransitionsTests
{
    [Test]
    public void CancelFlushAndRecovery_AllowOnlyNonTerminalSources()
    {
        string[] nonTerminal = [NodeChatMessageStatusValues.Pending, NodeChatMessageStatusValues.Queued, NodeChatMessageStatusValues.Streaming];

        AssertSameSet(nonTerminal, NodeChatMessageTransitions.CancelSources);
        AssertSameSet(nonTerminal, NodeChatMessageTransitions.FlushSources);
        AssertSameSet(nonTerminal, NodeChatMessageTransitions.RecoverySources);
    }

    [Test]
    [Arguments(NodeChatMessageStatusValues.Completed)]
    [Arguments(NodeChatMessageStatusValues.Failed)]
    [Arguments(NodeChatMessageStatusValues.Cancelled)]
    public void TerminalizeSources_ForTrueOutcomeTerminals_WhitelistCancelled(string terminalStatus)
    {
        var sources = NodeChatMessageTransitions.TerminalizeSources(terminalStatus);

        AssertSameSet(
            [
                NodeChatMessageStatusValues.Pending,
                NodeChatMessageStatusValues.Queued,
                NodeChatMessageStatusValues.Streaming,
                NodeChatMessageStatusValues.Cancelled
            ],
            sources);
    }

    [Test]
    public void TerminalizeSources_ForInterrupted_ExcludeCancelled()
    {
        var sources = NodeChatMessageTransitions.TerminalizeSources(NodeChatMessageStatusValues.Interrupted);

        AssertSameSet(
            [NodeChatMessageStatusValues.Pending, NodeChatMessageStatusValues.Queued, NodeChatMessageStatusValues.Streaming],
            sources);
        AssertEx.False(sources.Contains(NodeChatMessageStatusValues.Cancelled), "interrupted must never overwrite a cancelled row");
    }

    [Test]
    public void NoAllowedSourceSet_ContainsATerminalOtherThanCancelled()
    {
        // Completed / failed / interrupted are never a legal source for any intent — only Cancelled is ever whitelisted.
        string[] intents =
        [
            NodeChatMessageStatusValues.Completed,
            NodeChatMessageStatusValues.Failed,
            NodeChatMessageStatusValues.Cancelled,
            NodeChatMessageStatusValues.Interrupted
        ];

        foreach (var terminalStatus in intents)
        {
            var sources = NodeChatMessageTransitions.TerminalizeSources(terminalStatus);
            AssertEx.False(sources.Contains(NodeChatMessageStatusValues.Completed));
            AssertEx.False(sources.Contains(NodeChatMessageStatusValues.Failed));
            AssertEx.False(sources.Contains(NodeChatMessageStatusValues.Interrupted));
        }
    }

    private static void AssertSameSet(string[] expected, IReadOnlySet<string> actual)
    {
        AssertEx.Equal(expected.Length, actual.Count);
        foreach (var value in expected)
        {
            AssertEx.True(actual.Contains(value), $"expected source set to contain '{value}'");
        }
    }
}
