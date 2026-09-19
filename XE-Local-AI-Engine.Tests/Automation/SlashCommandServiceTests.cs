namespace XE_Local_AI_Engine.Tests.Automation;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Automation;
using XE_Local_AI_Engine.Client.Services.Automation.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Unit)]
public sealed class SlashCommandServiceTests
{
    [Test]
    public async Task CreateAsync_NormalizesOuterWhitespaceAndPreservesInternalPromptWhitespace()
    {
        var store = Substitute.For<ISlashCommandStore>();
        SlashCommandInput? captured = null;
        store.AddAsync(Arg.Do<SlashCommandInput>(input => captured = input), Arg.Any<CancellationToken>())
             .Returns(call => new SlashCommandRecord
             {
                 Id = Guid.NewGuid(),
                 Name = call.Arg<SlashCommandInput>().Name,
                 Description = call.Arg<SlashCommandInput>().Description,
                 ActionType = SlashCommandActionType.SendPrompt,
                 Prompt = call.Arg<SlashCommandInput>().Prompt,
                 CreatedAtUtc = 1,
                 UpdatedAtUtc = 1
             });
        var service = new SlashCommandService(store);

        var result = await service.CreateAsync(new SlashCommandInput { Name = " review ", Description = "  Review changes  ", ActionType = SlashCommandActionType.SendPrompt, Prompt = "  line one\n  line two  " });

        var input = AssertEx.NotNull(captured);
        AssertEx.Equal("review", input.Name);
        AssertEx.Equal("Review changes", input.Description);
        AssertEx.Equal("line one\n  line two", input.Prompt);
        AssertEx.Equal("review", result.Name);
    }

    [Test]
    [Arguments("ping")]
    [Arguments("Ping")]
    [Arguments("bad_name")]
    [Arguments("-bad")]
    public async Task CreateAsync_WithReservedOrInvalidName_RejectsBeforePersistence(string name)
    {
        var store = Substitute.For<ISlashCommandStore>();
        var service = new SlashCommandService(store);

        await AssertEx.ThrowsAsync<SlashCommandValidationException>(() =>
            service.CreateAsync(new SlashCommandInput { Name = name, Description = null, ActionType = SlashCommandActionType.SendPrompt, Prompt = "prompt" }));
        await store.DidNotReceive().AddAsync(Arg.Any<SlashCommandInput>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ListAsync_MergesBuiltinOnceAndSortsByCanonicalName()
    {
        var store = Substitute.For<ISlashCommandStore>();
        store.ListAsync(Arg.Any<CancellationToken>()).Returns([
            new SlashCommandRecord { Id = Guid.NewGuid(), Name = "review", Description = null, ActionType = SlashCommandActionType.SendPrompt, Prompt = "review", CreatedAtUtc = 1, UpdatedAtUtc = 1 },
            new SlashCommandRecord { Id = Guid.NewGuid(), Name = "alpha", Description = null, ActionType = SlashCommandActionType.SendPrompt, Prompt = "alpha", CreatedAtUtc = 1, UpdatedAtUtc = 1 }
        ]);
        var service = new SlashCommandService(store);

        var items = await service.ListAsync();

        AssertEx.Equal(expected: 3, items.Count);
        AssertEx.Equal("alpha", items[0].Name);
        AssertEx.Equal("ping", items[1].Name);
        AssertEx.Null(items[1].Id);
        AssertEx.Equal("builtIn", items[1].Source);
        AssertEx.Equal("review", items[2].Name);
    }

    [Test]
    public async Task CreateAsync_EnforcesUtf8ByteBoundaries()
    {
        var store = Substitute.For<ISlashCommandStore>();
        var service = new SlashCommandService(store);
        var overLimit = string.Concat(Enumerable.Repeat("😀", 5_001));

        await AssertEx.ThrowsAsync<SlashCommandValidationException>(() =>
            service.CreateAsync(new SlashCommandInput { Name = "review", Description = null, ActionType = SlashCommandActionType.SendPrompt, Prompt = overLimit }));
        await store.DidNotReceive().AddAsync(Arg.Any<SlashCommandInput>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CreateAsync_WhenSqliteUniqueNameConstraintFails_TranslatesConflict()
    {
        var store = Substitute.For<ISlashCommandStore>();
        store.AddAsync(Arg.Any<SlashCommandInput>(), Arg.Any<CancellationToken>())
             .Returns<SlashCommandRecord>(_ => throw new DbUpdateException("write", new SqliteException("unique", errorCode: 19, extendedErrorCode: 2067)));
        var service = new SlashCommandService(store);

        _ = await AssertEx.ThrowsAsync<SlashCommandConflictException>(() =>
            service.CreateAsync(new SlashCommandInput { Name = "review", Description = null, ActionType = SlashCommandActionType.SendPrompt, Prompt = "prompt" }));
    }

    [Test]
    public async Task CreateAsync_WhenUnrelatedDatabaseUpdateFails_PropagatesOriginalFailure()
    {
        var store = Substitute.For<ISlashCommandStore>();
        var failure = new DbUpdateException("disk failure", new SqliteException("io", errorCode: 10));
        store.AddAsync(Arg.Any<SlashCommandInput>(), Arg.Any<CancellationToken>()).Returns<SlashCommandRecord>(_ => throw failure);
        var service = new SlashCommandService(store);

        var actual = await AssertEx.ThrowsAsync<DbUpdateException>(() =>
            service.CreateAsync(new SlashCommandInput { Name = "review", Description = null, ActionType = SlashCommandActionType.SendPrompt, Prompt = "prompt" }));
        AssertEx.True(ReferenceEquals(failure, actual), "Unrelated database failures must propagate unchanged.");
    }
}
