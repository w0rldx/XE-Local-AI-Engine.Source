namespace XE_Local_AI_Engine.Tests.Chat;

using System.Reflection;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class NodeChatPersistenceContractTests
{
    [Test]
    public void Interface_DefinesPhase44PersistenceOperations()
    {
        var methods = typeof(INodeChatPersistenceService)
                      .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                      .Select(method => method.Name)
                      .ToHashSet(StringComparer.Ordinal);

        var expected = new[]
        {
            nameof(INodeChatPersistenceService.CreateConversationAsync),
            nameof(INodeChatPersistenceService.ListConversationsAsync),
            nameof(INodeChatPersistenceService.GetConversationAsync),
            nameof(INodeChatPersistenceService.PersistUserMessageAsync),
            nameof(INodeChatPersistenceService.CreateAssistantPlaceholderAsync),
            nameof(INodeChatPersistenceService.MarkAssistantStreamingAsync),
            nameof(INodeChatPersistenceService.FlushAssistantPartialAsync),
            nameof(INodeChatPersistenceService.TerminalizeAssistantMessageAsync),
            nameof(INodeChatPersistenceService.CancelMessageAsync),
            nameof(INodeChatPersistenceService.DeleteConversationAsync)
        };

        foreach (var method in expected)
        {
            AssertEx.Contains(methods, method);
        }
    }

    [Test]
    public void StatusValues_MatchAcceptedLifecycleContract()
    {
        var expected = new[]
        {
            "pending",
            "streaming",
            "completed",
            "cancelled",
            "failed",
            "interrupted"
        };

        AssertEx.Equal(expected.Length, NodeChatMessageStatusValues.All.Count);
        foreach (var status in expected)
        {
            AssertEx.Contains(NodeChatMessageStatusValues.All, status);
        }
    }

    [Test]
    public void CorrelatedOperations_RequireConversationMessageAndRequestIds()
    {
        var correlation = new NodeChatMessageCorrelation(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var cancel = new NodeChatCancelRequest(correlation, 42);
        var flush = new NodeChatPartialFlushRequest(correlation, "partial", "thinking", 43);
        var terminal = new NodeChatTerminalizeMessageRequest(correlation, NodeChatMessageStatusValues.Completed, 44);

        AssertEx.Equal(correlation, cancel.Correlation);
        AssertEx.Equal(correlation, flush.Correlation);
        AssertEx.Equal(correlation, terminal.Correlation);
        AssertEx.NotEqual(Guid.Empty, correlation.ConversationId);
        AssertEx.NotEqual(Guid.Empty, correlation.MessageId);
        AssertEx.NotEqual(Guid.Empty, correlation.RequestId);
    }

    [Test]
    public void Dtos_DoNotExposeEfEntitiesOrSecretBearingNames()
    {
        var dtoTypes = typeof(NodeChatConversationDto).Assembly
                                                      .GetTypes()
                                                      .Where(type => type.Namespace == typeof(NodeChatConversationDto).Namespace
                                                                     && type.Name.StartsWith("NodeChat", StringComparison.Ordinal)
                                                                     && type != typeof(INodeChatPersistenceService))
                                                      .ToArray();

        AssertEx.True(dtoTypes.Length > 0, "Expected node chat DTO types to be discoverable.");

        foreach (var type in dtoTypes)
        {
            AssertEx.False(type.FullName?.Contains("Persistence.Entities", StringComparison.Ordinal) == true, $"{type.Name} must not expose EF entities.");

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                AssertEx.False(IsSecretBearingName(property.Name), $"{type.Name}.{property.Name} must not expose secrets.");
                AssertEx.False(property.PropertyType.FullName?.Contains("Persistence.Entities", StringComparison.Ordinal) == true, $"{type.Name}.{property.Name} must not expose EF entities.");
            }
        }
    }

    [Test]
    public void AsyncContracts_AcceptCancellationTokens()
    {
        var methods = typeof(INodeChatPersistenceService).GetMethods(BindingFlags.Public | BindingFlags.Instance);

        foreach (var method in methods)
        {
            var parameters = method.GetParameters();
            AssertEx.True(parameters.Length > 0, $"{method.Name} should accept at least one parameter.");
            AssertEx.Equal(typeof(CancellationToken), parameters[^1].ParameterType, $"{method.Name} should end with CancellationToken.");
        }
    }

    private static bool IsSecretBearingName(string name)
    {
        return name.Contains("secret", StringComparison.OrdinalIgnoreCase)
               || name.Contains("credential", StringComparison.OrdinalIgnoreCase)
               || name.Contains("apiKey", StringComparison.OrdinalIgnoreCase)
               || name.Contains("token", StringComparison.OrdinalIgnoreCase)
               || name.Contains("password", StringComparison.OrdinalIgnoreCase);
    }
}
