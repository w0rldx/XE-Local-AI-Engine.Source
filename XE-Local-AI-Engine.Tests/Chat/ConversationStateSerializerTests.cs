namespace XE_Local_AI_Engine.Tests.Chat;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Services.Chat.Compaction.State;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Unit)]
public sealed class ConversationStateSerializerTests
{
    [Test]
    public void RoundTrip_PreservesEveryPersistedField()
    {
        var document = new ConversationStateDocument
        {
            Entries =
            [
                new ConversationStateEntry
                {
                    Id = "e1", Category = ConversationStateCategory.Decision, Value = "use sqlite", SourceSequences = [2, 4],
                    CreatedAtSequence = 4, SupersededById = "e2"
                },
                new ConversationStateEntry
                {
                    Id = "e2", Category = ConversationStateCategory.OpenQuestion, Value = "which schema? 中", SourceSequences = [5],
                    CreatedAtSequence = 5, RetiredAtSequence = 9
                }
            ]
        };

        var restored = AssertEx.NotNull(ConversationStateSerializer.Deserialize(ConversationStateSerializer.Serialize(document)));

        AssertEx.Equal(ConversationStateDocument.CurrentVersion, restored.Version);
        AssertEx.Equal(2, restored.Entries.Count);
        var first = restored.Entries[0];
        AssertEx.Equal("e1", first.Id);
        AssertEx.Equal(ConversationStateCategory.Decision, first.Category);
        AssertEx.Equal("use sqlite", first.Value);
        AssertEx.Equal("2,4", string.Join(',', first.SourceSequences));
        AssertEx.Equal(4, first.CreatedAtSequence);
        AssertEx.Equal("e2", first.SupersededById);
        AssertEx.Null(first.RetiredAtSequence);
        var second = restored.Entries[1];
        AssertEx.Equal("which schema? 中", second.Value);
        AssertEx.Equal(9, second.RetiredAtSequence);
        AssertEx.Null(second.SupersededById);
    }

    [Test]
    public void Serialize_WritesCamelCaseNamesAndEnumStrings_AndOmitsIsLive()
    {
        var json = ConversationStateSerializer.Serialize(new ConversationStateDocument
        {
            Entries = [new ConversationStateEntry { Id = "e1", Category = ConversationStateCategory.ToolOutcome, Value = "ok 中", SourceSequences = [1], CreatedAtSequence = 1 }]
        });

        using var parsed = JsonDocument.Parse(json);
        var entry = parsed.RootElement.GetProperty("entries")[0];
        AssertEx.Equal("ToolOutcome", entry.GetProperty("category").GetString());
        AssertEx.True(parsed.RootElement.TryGetProperty("version", out _));
        AssertEx.False(entry.TryGetProperty("isLive", out _), "IsLive is derived and must not be persisted.");
        AssertEx.Contains(json, "中", message: "Relaxed escaping keeps non-ASCII text unescaped.");
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("{not json")]
    [Arguments("[]")]
    [Arguments("{\"version\":1,\"entries\":null}")]
    [Arguments("{\"version\":1,\"entries\":[null]}")]
    [Arguments("{\"version\":1,\"entries\":[{\"id\":\"e1\",\"category\":\"NoSuchCategory\",\"value\":\"v\",\"sourceSequences\":[],\"createdAtSequence\":1}]}")]
    [Arguments("{\"version\":1,\"entries\":[{\"id\":\"e1\",\"category\":3,\"value\":\"v\",\"sourceSequences\":[],\"createdAtSequence\":1}]}")]
    [Arguments("{\"version\":1,\"entries\":[{\"id\":\"e1\",\"category\":\"Fact\",\"sourceSequences\":[],\"createdAtSequence\":1}]}")]
    [Arguments("{\"version\":1,\"entries\":[{\"id\":\"e1\",\"category\":\"Fact\",\"value\":null,\"sourceSequences\":[],\"createdAtSequence\":1}]}")]
    public void Deserialize_MissingOrCorruptInput_ReturnsNull(string? json)
    {
        AssertEx.Null(ConversationStateSerializer.Deserialize(json));
    }

    [Test]
    public void Deserialize_OtherVersion_ReturnsNull()
    {
        var json = ConversationStateSerializer.Serialize(new ConversationStateDocument { Version = ConversationStateDocument.CurrentVersion + 1 });

        AssertEx.Null(ConversationStateSerializer.Deserialize(json));
    }

    [Test]
    public void Deserialize_EmptyCurrentVersionDocument_ReturnsAnEmptyDocument()
    {
        var restored = AssertEx.NotNull(ConversationStateSerializer.Deserialize("{\"version\":1,\"entries\":[]}"));

        AssertEx.Empty(restored.Entries);
    }
}
