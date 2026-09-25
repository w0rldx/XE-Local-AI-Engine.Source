namespace XE_Local_AI_Engine.Tests.Chat;

using XE_Local_AI_Engine.Client.Services.Chat.Compaction;
using XE_Local_AI_Engine.Client.Services.Chat.Compaction.State;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Unit)]
public sealed class ConversationStateReducerTests
{
    private static readonly ConversationCompactionOptions Roomy = new();

    [Test]
    public void Apply_MintsSequentialIdsAfterTheLargestExistingSuffix()
    {
        var document = Doc(Entry("e3", ConversationStateCategory.Fact, "a", 1), Entry("e10", ConversationStateCategory.Fact, "b", 2));

        var result = ConversationStateReducer.Apply(document, Adds(("x", ConversationStateCategory.Goal), ("y", ConversationStateCategory.Fact)), 7, Roomy);

        AssertEx.Equal("e3,e10,e11,e12", string.Join(',', result.Document.Entries.Select(static entry => entry.Id)));
        AssertEx.True(result.Document.Entries.Skip(2).All(static entry => entry.CreatedAtSequence == 7 && entry.IsLive));
        AssertEx.Empty(result.Rejections);
    }

    [Test]
    public void Apply_OnAnEmptyDocument_StartsAtE1()
    {
        var result = ConversationStateReducer.Apply(new ConversationStateDocument(), Adds(("x", ConversationStateCategory.Goal)), 1, Roomy);

        AssertEx.Equal("e1", result.Document.Entries.Single().Id);
    }

    [Test]
    public void Apply_DedupesAndSortsSourceSequences_AndTrimsTheValue()
    {
        var delta = new ConversationStateDelta
        {
            Add =
            [
                new ConversationStateProposedEntry
                {
                    Category = ConversationStateCategory.Fact,
                    Value = "  padded  ",
                    SourceSequences = [9, 3, 9, 5]
                }
            ]
        };

        var entry = ConversationStateReducer.Apply(new ConversationStateDocument(), delta, 9, Roomy).Document.Entries.Single();

        AssertEx.Equal("3,5,9", string.Join(',', entry.SourceSequences));
        AssertEx.Equal("padded", entry.Value);
    }

    [Test]
    public void Apply_BlankValue_IsRejectedAndMintsNoId()
    {
        var result = ConversationStateReducer.Apply(new ConversationStateDocument(), Adds(("   ", ConversationStateCategory.Fact), ("real", ConversationStateCategory.Fact)), 1, Roomy);

        AssertEx.Equal("e1", result.Document.Entries.Single().Id);
        AssertEx.ContainsSingle(result.Rejections, static rejection => rejection.Operation == "add" && rejection.EntryId is null);
    }

    [Test]
    public void Apply_ValueLongerThanTheCap_IsCutAtARuneBoundary()
    {
        // 499 ASCII characters then a surrogate pair straddling the 500-character cut.
        var value = new string('a', ConversationStateReducer.MaxValueChars - 1) + "\U0001F600tail";

        var entry = ConversationStateReducer.Apply(new ConversationStateDocument(), Adds((value, ConversationStateCategory.Fact)), 1, Roomy).Document.Entries.Single();

        AssertEx.Equal(new string('a', ConversationStateReducer.MaxValueChars - 1), entry.Value);
    }

    [Test]
    public void Apply_AddThatSupersedesALiveEntry_LinksTheOldEntryToTheNewId()
    {
        var document = Doc(Entry("e1", ConversationStateCategory.Decision, "use sqlite", 1));
        var delta = new ConversationStateDelta
        {
            Add =
            [
                new ConversationStateProposedEntry
                {
                    Category = ConversationStateCategory.Correction,
                    Value = "use postgres",
                    Supersedes = ["e1"]
                }
            ]
        };

        var result = ConversationStateReducer.Apply(document, delta, 4, Roomy);

        var old = result.Document.Entries.Single(static entry => entry.Id == "e1");
        AssertEx.Equal("e2", old.SupersededById);
        AssertEx.False(old.IsLive);
        AssertEx.True(result.Document.Entries.Single(static entry => entry.Id == "e2").IsLive);
        AssertEx.Empty(result.Rejections);
    }

    [Test]
    public void Apply_AddThatSupersedesAnUnknownOrDeadId_IsReportedButStillAdded()
    {
        var document = Doc(Retired(Entry("e1", ConversationStateCategory.Fact, "old", 1), 2));
        var delta = new ConversationStateDelta
        {
            Add =
            [
                new ConversationStateProposedEntry
                {
                    Category = ConversationStateCategory.Fact,
                    Value = "new",
                    Supersedes = ["e1", "e99"]
                }
            ]
        };

        var result = ConversationStateReducer.Apply(document, delta, 5, Roomy);

        AssertEx.True(result.Document.Entries.Any(static entry => entry is { Id: "e2", Value: "new" }));
        AssertEx.Null(result.Document.Entries.Single(static entry => entry.Id == "e1").SupersededById);
        AssertEx.Equal("e1,e99", string.Join(',', result.Rejections.Select(static rejection => rejection.EntryId)));
    }

    [Test]
    public void Apply_SupersedeRetiresALiveEntryAtTheGivenSequence_AndRejectsUnknownOrDeadIds()
    {
        var document = Doc(Entry("e1", ConversationStateCategory.Constraint, "no network", 1), Retired(Entry("e2", ConversationStateCategory.Fact, "x", 1), 3));
        var delta = new ConversationStateDelta
        {
            Supersede =
            [
                new ConversationStateRetirement
                {
                    EntryId = "e1",
                    BySequence = 8
                },
                new ConversationStateRetirement
                {
                    EntryId = "e2",
                    BySequence = 8
                },
                new ConversationStateRetirement
                {
                    EntryId = "e7",
                    BySequence = 8
                }
            ]
        };

        var result = ConversationStateReducer.Apply(document, delta, 9, Roomy);

        AssertEx.Equal(8, result.Document.Entries.Single(static entry => entry.Id == "e1").RetiredAtSequence);
        AssertEx.Equal(3, result.Document.Entries.Single(static entry => entry.Id == "e2").RetiredAtSequence);
        AssertEx.Equal("e2,e7", string.Join(',', result.Rejections.Where(static rejection => rejection.Operation == "supersede").Select(static rejection => rejection.EntryId)));
    }

    [Test]
    public void Apply_ResolveRetiresOnlyLiveOpenQuestions()
    {
        var document = Doc(Entry("e1", ConversationStateCategory.OpenQuestion, "which db?", 1), Entry("e2", ConversationStateCategory.Goal, "ship", 1));
        var delta = new ConversationStateDelta
        {
            Resolve = ["e1", "e2", "e5"]
        };

        var result = ConversationStateReducer.Apply(document, delta, 6, Roomy);

        AssertEx.Equal(6, result.Document.Entries.Single(static entry => entry.Id == "e1").RetiredAtSequence);
        AssertEx.True(result.Document.Entries.Single(static entry => entry.Id == "e2").IsLive);
        AssertEx.Equal("e2,e5", string.Join(',', result.Rejections.Where(static rejection => rejection.Operation == "resolve").Select(static rejection => rejection.EntryId)));
    }

    [Test]
    public void Apply_EmptyDelta_ReturnsAnEqualDocumentWithoutRejections()
    {
        var document = Doc(Entry("e4", ConversationStateCategory.Fact, "a", 1));

        var result = ConversationStateReducer.Apply(document, new ConversationStateDelta(), 10, Roomy);

        AssertEx.Equal(ConversationStateSerializer.Serialize(document), ConversationStateSerializer.Serialize(result.Document));
        AssertEx.Empty(result.Rejections);
        AssertEx.Empty(result.DroppedIds);
    }

    [Test]
    public void Apply_OverTheEntryCap_DropsNonLiveEntriesOldestFirst()
    {
        var document = Doc(Entry("e1", ConversationStateCategory.Fact, "live fact", 1),
            Retired(Entry("e3", ConversationStateCategory.Goal, "dead newer", 5), 6),
            Retired(Entry("e2", ConversationStateCategory.Goal, "dead older", 2), 6));

        var result = ConversationStateReducer.Apply(document, Adds(("new", ConversationStateCategory.Fact)), 7, Caps(entries: 3));

        AssertEx.Equal("e2", string.Join(',', result.DroppedIds));
        AssertEx.Equal("e1,e3,e4", string.Join(',', result.Document.Entries.Select(static entry => entry.Id)));
    }

    [Test]
    public void Apply_WhenNonLiveAreGone_DropsRoutineLiveCategoriesBeforeIntent()
    {
        var document = Doc(Entry("e1", ConversationStateCategory.Goal, "goal", 1),
            Entry("e2", ConversationStateCategory.ToolOutcome, "tool", 2),
            Entry("e3", ConversationStateCategory.CompletedWork, "done", 1),
            Retired(Entry("e4", ConversationStateCategory.Fact, "dead", 3), 4));

        var result = ConversationStateReducer.Apply(document, Adds(("fact", ConversationStateCategory.Fact)), 5, Caps(entries: 2));

        AssertEx.Equal("e4,e3,e2", string.Join(',', result.DroppedIds));
        AssertEx.Equal("e1,e5", string.Join(',', result.Document.Entries.Select(static entry => entry.Id)));
    }

    [Test]
    public void Apply_WhenIntentAloneExceedsTheCap_DropsIntentOldestFirst()
    {
        var document = Doc(Entry("e2", ConversationStateCategory.Decision, "later decision", 3),
            Entry("e1", ConversationStateCategory.Constraint, "early constraint", 3));

        var result = ConversationStateReducer.Apply(document, Adds(("goal", ConversationStateCategory.Goal)), 4, Caps(entries: 1));

        AssertEx.Equal("e1,e2", string.Join(',', result.DroppedIds), "Same CreatedAtSequence breaks the tie by id number.");
        AssertEx.Equal("e3", result.Document.Entries.Single().Id);
    }

    [Test]
    public void Apply_OverTheCharCap_DropsUntilTheSummedValuesFit()
    {
        var document = Doc(Entry("e1", ConversationStateCategory.Fact, new string('a', 400), 1),
            Entry("e2", ConversationStateCategory.Goal, new string('b', 400), 1));

        var result = ConversationStateReducer.Apply(document, Adds((new string('c', 400), ConversationStateCategory.Fact)), 2, Caps(chars: 1000));

        AssertEx.Equal("e1", string.Join(',', result.DroppedIds));
        AssertEx.True(result.Document.Entries.Sum(static entry => entry.Value.Length) <= 1000);
    }

    [Test]
    public void Apply_DroppingASupersededEntry_KeepsThePointerAndNeverReusesItsId()
    {
        var first = ConversationStateReducer.Apply(new ConversationStateDocument(), Adds(("a", ConversationStateCategory.Fact), ("b", ConversationStateCategory.Fact)), 1, Roomy).Document;
        var supersede = new ConversationStateDelta
        {
            Add =
            [
                new ConversationStateProposedEntry
                {
                    Category = ConversationStateCategory.Fact,
                    Value = "b2",
                    Supersedes = ["e2"]
                }
            ]
        };

        var second = ConversationStateReducer.Apply(first, supersede, 2, Caps(entries: 2));
        var third = ConversationStateReducer.Apply(second.Document, Adds(("c", ConversationStateCategory.Goal)), 3, Caps(entries: 10));

        AssertEx.Equal("e2", string.Join(',', second.DroppedIds));
        AssertEx.Equal("e1,e3,e4", string.Join(',', third.Document.Entries.Select(static entry => entry.Id)),
            "The dropped e2 must not be reminted.");
    }

    [Test]
    public void Apply_UsesThePersistedCounterOverTheLargestIdPresent()
    {
        var document = new ConversationStateDocument
        {
            NextEntryNumber = 6,
            Entries =
            [
                new ConversationStateEntry
                {
                    Id = "e1",
                    Category = ConversationStateCategory.Goal,
                    Value = "old goal",
                    SourceSequences = [1],
                    CreatedAtSequence = 1,
                    SupersededById = "e5"
                }
            ]
        };

        var result = ConversationStateReducer.Apply(document, Adds(("next", ConversationStateCategory.Fact)), 6, Roomy);

        AssertEx.Equal("e6", result.Document.Entries[^1].Id);
        AssertEx.Equal(7, result.Document.NextEntryNumber);
    }

    [Test]
    public void Apply_WhenTheBudgetDropsTheNewestEntry_TheNextDeltaDoesNotReuseItsId()
    {
        // e1 is a live Goal (last tier); the new Fact e2 is the only lower-tier victim and it is the newest id.
        var first = ConversationStateReducer.Apply(new ConversationStateDocument(), Adds(("goal", ConversationStateCategory.Goal)), 1, Roomy).Document;
        var second = ConversationStateReducer.Apply(first, Adds(("fact", ConversationStateCategory.Fact)), 2, Caps(entries: 1));
        var third = ConversationStateReducer.Apply(second.Document, Adds(("later", ConversationStateCategory.Fact)), 3, Roomy);

        AssertEx.Equal("e2", string.Join(',', second.DroppedIds));
        AssertEx.Equal("e1,e3", string.Join(',', third.Document.Entries.Select(static entry => entry.Id)));
    }

    private static ConversationCompactionOptions Caps(int entries = 500, int chars = 50_000)
    {
        return new ConversationCompactionOptions
        {
            MaxStateEntries = entries,
            MaxStateChars = chars
        };
    }

    private static ConversationStateDelta Adds(params (string Value, ConversationStateCategory Category)[] adds)
    {
        return new ConversationStateDelta
        {
            Add = adds.Select(static add => new ConversationStateProposedEntry
            {
                Category = add.Category,
                Value = add.Value
            }).ToList()
        };
    }

    private static ConversationStateDocument Doc(params ConversationStateEntry[] entries)
    {
        return new ConversationStateDocument
        {
            Entries = entries
        };
    }

    private static ConversationStateEntry Entry(string id, ConversationStateCategory category, string value, int createdAt)
    {
        return new ConversationStateEntry
        {
            Id = id,
            Category = category,
            Value = value,
            SourceSequences = [createdAt],
            CreatedAtSequence = createdAt
        };
    }

    private static ConversationStateEntry Retired(ConversationStateEntry entry, int at)
    {
        return new ConversationStateEntry
        {
            Id = entry.Id,
            Category = entry.Category,
            Value = entry.Value,
            SourceSequences = entry.SourceSequences,
            CreatedAtSequence = entry.CreatedAtSequence,
            RetiredAtSequence = at
        };
    }
}
