namespace XE_Local_AI_Engine.Cli.Tests.Streaming;

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using XE_Local_AI_Engine.Cli.Streaming;
using XE_Local_AI_Engine.Tests.Testing;
using Server = XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>Pins the CLI's copies of the hub records to the server records they mirror, so a rename on either side fails here.</summary>
[Category(TestCategories.Unit)]
public sealed class ChatStreamDtoContractTests
{
    [Test]
    [Arguments(typeof(ChatStreamEvent), typeof(Server.ChatStreamEvent))]
    [Arguments(typeof(NodeChatStreamRequest), typeof(Server.NodeChatStreamRequest))]
    public void EveryCliMember_ExistsOnTheServerRecordWithTheSameJsonNameAndACompatibleType(Type cliType, Type serverType)
    {
        var serverMembers = JsonMembers(serverType);
        var cliMembers = JsonMembers(cliType);

        AssertEx.NotEmpty(cliMembers.Keys);
        foreach (var (name, cliMemberType) in cliMembers)
        {
            AssertEx.True(serverMembers.TryGetValue(name, out var serverMemberType),
                $"{cliType.Name}.{name} has no counterpart on the server's {serverType.FullName}.");
            AssertEx.Equal(Unwrap(serverMemberType!), Unwrap(cliMemberType), $"{cliType.Name}.{name} type");
        }
    }

    [Test]
    public void TheCliEventTypes_AreExactlyTheServerEventTypes()
    {
        AssertEx.Equal(string.Join(',', ConstantValues(typeof(Server.ChatStreamEventTypes))),
            string.Join(',', ConstantValues(typeof(ChatStreamEventTypes))));
    }

    [Test]
    public void EveryServerEventType_IsHandledByTheFolder()
    {
        foreach (var type in ConstantValues(typeof(Server.ChatStreamEventTypes)))
        {
            var outcome = new ChatStreamFolder(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()).Fold(new ChatStreamEvent
            {
                Type = type,
                ApprovalRequestId = "approval",
                QuestionRequestId = "question"
            });

            AssertEx.True(outcome.Event is not null || outcome.NeedsResume, $"'{type}' was dropped by the folder.");
        }
    }

    private static Dictionary<string, Type> JsonMembers(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(static property => property.GetCustomAttribute<JsonIgnoreAttribute>() is null && property.GetIndexParameters().Length == 0)
            .Where(static property => property.Name != "EqualityContract")
            .ToDictionary(static property => property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? JsonNamingPolicy.CamelCase.ConvertName(property.Name),
                static property => property.PropertyType,
                StringComparer.Ordinal);

    private static Type Unwrap(Type type) =>
        Nullable.GetUnderlyingType(type) ?? type;

    private static List<string> ConstantValues(Type type) =>
        type.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(static field => field is { IsLiteral: true, IsInitOnly: false } && field.FieldType == typeof(string))
            .Select(static field => (string)field.GetRawConstantValue()!)
            .Order(StringComparer.Ordinal)
            .ToList();
}
