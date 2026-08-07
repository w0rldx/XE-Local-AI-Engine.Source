namespace XE_Local_AI_Engine.Tests.Automation;

using NSubstitute;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Automation;
using XE_Local_AI_Engine.Client.Services.Automation.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class SlashCommandServiceTests
{
    [Test]
    public async Task CreateAsync_NormalizesOuterWhitespaceAndPreservesInternalPromptWhitespace()
    {
        var store = Substitute.For<ISlashCommandStore>();
        SlashCommandInput? captured = null;
        store.AddAsync(Arg.Do<SlashCommandInput>(input => captured = input), Arg.Any<CancellationToken>())
             .Returns(call => new SlashCommandRecord(Guid.NewGuid(), call.Arg<SlashCommandInput>().Name, call.Arg<SlashCommandInput>().Description,
                 SlashCommandActionType.SendPrompt, call.Arg<SlashCommandInput>().Prompt, 1, 1));
        var service = new SlashCommandService(store);

        var result = await service.CreateAsync(new SlashCommandInput(" review ", "  Review changes  ", SlashCommandActionType.SendPrompt, "  line one\n  line two  "));

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
            service.CreateAsync(new SlashCommandInput(name, null, SlashCommandActionType.SendPrompt, "prompt")));
        await store.DidNotReceive().AddAsync(Arg.Any<SlashCommandInput>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ListAsync_MergesBuiltinOnceAndSortsByCanonicalName()
    {
        var store = Substitute.For<ISlashCommandStore>();
        store.ListAsync(Arg.Any<CancellationToken>()).Returns([
            new SlashCommandRecord(Guid.NewGuid(), "review", null, SlashCommandActionType.SendPrompt, "review", 1, 1),
            new SlashCommandRecord(Guid.NewGuid(), "alpha", null, SlashCommandActionType.SendPrompt, "alpha", 1, 1)
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
            service.CreateAsync(new SlashCommandInput("review", null, SlashCommandActionType.SendPrompt, overLimit)));
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
            service.CreateAsync(new SlashCommandInput("review", null, SlashCommandActionType.SendPrompt, "prompt")));
    }

    [Test]
    public async Task CreateAsync_WhenUnrelatedDatabaseUpdateFails_PropagatesOriginalFailure()
    {
        var store = Substitute.For<ISlashCommandStore>();
        var failure = new DbUpdateException("disk failure", new SqliteException("io", errorCode: 10));
        store.AddAsync(Arg.Any<SlashCommandInput>(), Arg.Any<CancellationToken>()).Returns<SlashCommandRecord>(_ => throw failure);
        var service = new SlashCommandService(store);

        var actual = await AssertEx.ThrowsAsync<DbUpdateException>(() =>
            service.CreateAsync(new SlashCommandInput("review", null, SlashCommandActionType.SendPrompt, "prompt")));
        AssertEx.True(ReferenceEquals(failure, actual), "Unrelated database failures must propagate unchanged.");
    }
}
