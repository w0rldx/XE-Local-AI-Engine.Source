namespace XE_Local_AI_Engine.Client.Persistence.Tests.Integrations;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     The Fluent mappings, through a real context and a real SQLite file: what is unique, what is encrypted, and what
///     the AAD binds an encrypted column to.
/// </summary>
public sealed class IntegrationEntityConfigurationTests
{
    [Test]
    public async Task TriggerName_IsUniquePerNode()
    {
        using var fixture = new IntegrationTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);

        _ = context.IntegrationTriggers.Add(IntegrationTestFixture.Trigger());
        _ = await context.SaveChangesAsync().ConfigureAwait(false);

        _ = context.IntegrationTriggers.Add(IntegrationTestFixture.Trigger());
        var failure = await AssertEx.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync()).ConfigureAwait(false);
        _ = AssertEx.NotNull(failure.InnerException as SqliteException, "The trigger name is the external contract, so a duplicate must be refused by the database.");
    }

    [Test]
    public async Task RequestId_IsUniquePerPrincipalAndNotGlobally()
    {
        using var fixture = new IntegrationTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);

        var trigger = IntegrationTestFixture.Trigger();
        var principal = Guid.NewGuid();
        var otherPrincipal = Guid.NewGuid();
        var session = IntegrationTestFixture.Session(trigger.Id, principal);
        var requestId = Guid.NewGuid();

        _ = context.IntegrationTriggers.Add(trigger);
        _ = context.IntegrationSessions.Add(session);
        _ = context.IntegrationExecutions.Add(IntegrationTestFixture.Execution(trigger.Id, session.Id, principal, requestId: requestId));
        _ = await context.SaveChangesAsync().ConfigureAwait(false);

        // Same principal, same request id: the dedup key, so the second row must be refused.
        _ = context.IntegrationExecutions.Add(IntegrationTestFixture.Execution(trigger.Id, session.Id, principal, requestId: requestId));
        var failure = await AssertEx.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync()).ConfigureAwait(false);
        _ = AssertEx.NotNull(failure.InnerException as SqliteException);
        context.ChangeTracker.Clear();

        // A DIFFERENT principal replaying the same request id must insert cleanly. Ruling R4-6 replaced the global
        // unique index precisely so one integrator cannot preclaim another's request id and force it a permanent 409.
        var otherSession = IntegrationTestFixture.Session(trigger.Id, otherPrincipal);
        _ = context.IntegrationSessions.Add(otherSession);
        _ = context.IntegrationExecutions.Add(IntegrationTestFixture.Execution(trigger.Id, otherSession.Id, otherPrincipal, requestId: requestId));
        _ = await context.SaveChangesAsync().ConfigureAwait(false);

        AssertEx.Equal(expected: 2L, await fixture.RawTableCountAsync("integration_executions").ConfigureAwait(false));
    }

    [Test]
    public async Task EventSequence_IsUniquePerExecution()
    {
        using var fixture = new IntegrationTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);

        var trigger = IntegrationTestFixture.Trigger();
        var principal = Guid.NewGuid();
        var session = IntegrationTestFixture.Session(trigger.Id, principal);
        var execution = IntegrationTestFixture.Execution(trigger.Id, session.Id, principal);

        _ = context.IntegrationTriggers.Add(trigger);
        _ = context.IntegrationSessions.Add(session);
        _ = context.IntegrationExecutions.Add(execution);
        _ = context.IntegrationExecutionEvents.Add(IntegrationTestFixture.Event(execution.Id, sequence: 1, "execution.accepted"));
        _ = await context.SaveChangesAsync().ConfigureAwait(false);

        _ = context.IntegrationExecutionEvents.Add(IntegrationTestFixture.Event(execution.Id, sequence: 1, "execution.started"));
        var failure = await AssertEx.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync()).ConfigureAwait(false);
        _ = AssertEx.NotNull(failure.InnerException as SqliteException,
            "A duplicate (execution_id, sequence) means a caller minted a sequence it never reserved — a bug, not a race to swallow.");
    }

    [Test]
    public async Task EventDetailJson_RoundTripsAsPlaintextAndIsBoundToItsOwningExecution()
    {
        using var fixture = new IntegrationTestFixture();
        var payload = """{"reading":42}"""u8.ToArray();
        Guid victimEventId;
        Guid attackerEventId;

        await using (var context = await fixture.CreateSchemaAsync().ConfigureAwait(false))
        {
            var trigger = IntegrationTestFixture.Trigger();
            var principal = Guid.NewGuid();
            var session = IntegrationTestFixture.Session(trigger.Id, principal);
            var victimExecution = IntegrationTestFixture.Execution(trigger.Id, session.Id, principal);
            var attackerExecution = IntegrationTestFixture.Execution(trigger.Id, session.Id, principal);

            _ = context.IntegrationTriggers.Add(trigger);
            _ = context.IntegrationSessions.Add(session);
            context.IntegrationExecutions.AddRange(victimExecution, attackerExecution);

            var victimEvent = IntegrationTestFixture.Event(victimExecution.Id, sequence: 1, "external.output", payload);
            var attackerEvent = IntegrationTestFixture.Event(attackerExecution.Id, sequence: 1, "external.output", """{"reading":0}"""u8.ToArray());
            context.IntegrationExecutionEvents.AddRange(victimEvent, attackerEvent);
            _ = await context.SaveChangesAsync().ConfigureAwait(false);

            victimEventId = victimEvent.Id;
            attackerEventId = attackerEvent.Id;
        }

        await using (var readContext = fixture.CreateContext())
        {
            var read = AssertEx.NotNull(await readContext.IntegrationExecutionEvents.AsNoTracking().SingleOrDefaultAsync(entity => entity.Id == victimEventId)
                                                         .ConfigureAwait(false));
            AssertEx.Equal("""{"reading":42}""", Encoding.UTF8.GetString(AssertEx.NotNull(read.DetailJson)),
                "The materialization interceptor must decrypt detail_json, or every reader gets ciphertext.");
        }

        var stored = AssertEx.NotNull(await fixture.RawScalarAsync("SELECT detail_json FROM integration_execution_events WHERE id = $id;",
                                                       command => command.Parameters.AddWithValue("$id", victimEventId))
                                                   .ConfigureAwait(false)) as byte[];
        AssertEx.False(AssertEx.NotNull(stored).AsSpan().IndexOf(payload) >= 0, "The payload must not survive as plaintext in the file.");

        // Re-parent the ciphertext onto another execution's event row: the AAD binds the owning execution, so the copy
        // must fail its tag check rather than read back as that execution's output.
        await fixture.RawExecuteAsync("UPDATE integration_execution_events SET detail_json = $payload WHERE id = $id;",
                         command =>
                         {
                             command.Parameters.AddWithValue("$payload", stored!);
                             command.Parameters.AddWithValue("$id", attackerEventId);
                         })
                     .ConfigureAwait(false);

        await using (var attackContext = fixture.CreateContext())
        {
            _ = AssertEx.Throws<CryptographicException>(
                () => _ = attackContext.IntegrationExecutionEvents.AsNoTracking().SingleOrDefault(entity => entity.Id == attackerEventId),
                "A re-parented event row must fail authentication instead of reading back as another execution's output.");
        }
    }

    [Test]
    public async Task ApiKeyHash_RoundTripsThroughTheRequiredEncryptedPathAndIsBoundToItsRow()
    {
        using var fixture = new IntegrationTestFixture();
        var digest = SHA256.HashData("xeint_a1b2c3d4.the-secret"u8.ToArray());
        Guid victimKeyId;
        Guid attackerKeyId;

        await using (var context = await fixture.CreateSchemaAsync().ConfigureAwait(false))
        {
            var victim = IntegrationTestFixture.ApiKey("xeint_aaaaaaaa", keyHash: digest);
            var attacker = IntegrationTestFixture.ApiKey("xeint_bbbbbbbb", keyHash: SHA256.HashData("other"u8.ToArray()));
            context.IntegrationApiKeys.AddRange(victim, attacker);
            _ = await context.SaveChangesAsync().ConfigureAwait(false);
            victimKeyId = victim.Id;
            attackerKeyId = attacker.Id;
        }

        await using (var readContext = fixture.CreateContext())
        {
            var read = AssertEx.NotNull(await readContext.IntegrationApiKeys.AsNoTracking().SingleOrDefaultAsync(entity => entity.Id == victimKeyId)
                                                         .ConfigureAwait(false));
            AssertEx.True(read.KeyHash.AsSpan().SequenceEqual(digest), "A required encrypted column must read back as its plaintext digest.");
        }

        var stored = AssertEx.NotNull(await fixture.RawScalarAsync("SELECT key_hash FROM integration_api_keys WHERE id = $id;",
                                                       command => command.Parameters.AddWithValue("$id", victimKeyId))
                                                   .ConfigureAwait(false)) as byte[];
        AssertEx.False(AssertEx.NotNull(stored).AsSpan().IndexOf(digest.AsSpan()) >= 0,
            "The digest is sealed at rest: a database-file WRITER must not be able to read it out and substitute a preimage they know.");

        await fixture.RawExecuteAsync("UPDATE integration_api_keys SET key_hash = $payload WHERE id = $id;",
                         command =>
                         {
                             command.Parameters.AddWithValue("$payload", stored!);
                             command.Parameters.AddWithValue("$id", attackerKeyId);
                         })
                     .ConfigureAwait(false);

        await using (var attackContext = fixture.CreateContext())
        {
            _ = AssertEx.Throws<CryptographicException>(() => _ = attackContext.IntegrationApiKeys.AsNoTracking().SingleOrDefault(entity => entity.Id == attackerKeyId),
                "Copying one key row's sealed digest onto another must fail authentication — that substitution is exactly what the AAD exists to stop.");
        }
    }

    [Test]
    public async Task FlagsInputKinds_RoundTripAsACombinedIntegerColumn()
    {
        using var fixture = new IntegrationTestFixture();
        var trigger = IntegrationTestFixture.Trigger();

        await using (var context = await fixture.CreateSchemaAsync().ConfigureAwait(false))
        {
            _ = context.IntegrationTriggers.Add(trigger);
            _ = await context.SaveChangesAsync().ConfigureAwait(false);
        }

        AssertEx.Equal(expected: 3L,
            Convert.ToInt64(await fixture.RawScalarAsync("SELECT accepted_input_kinds FROM integration_triggers;").ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture),
            "A [Flags] combination is stored as an int; a string conversion would write \"Text, Json\", whose text depends on declaration order.");

        await using (var readContext = fixture.CreateContext())
        {
            var read = AssertEx.NotNull(await readContext.IntegrationTriggers.AsNoTracking().SingleOrDefaultAsync(entity => entity.Id == trigger.Id).ConfigureAwait(false));
            AssertEx.Equal(IntegrationInputKinds.Text | IntegrationInputKinds.Json, read.AcceptedInputKinds);
        }
    }
}
