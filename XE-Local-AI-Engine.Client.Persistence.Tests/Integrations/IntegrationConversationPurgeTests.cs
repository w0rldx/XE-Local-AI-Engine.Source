namespace XE_Local_AI_Engine.Client.Persistence.Tests.Integrations;

using System.Globalization;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     An integration session owns its conversation, so a conversation purge must take the session, its executions and
///     their events with it. <c>ConversationFootprintPurgeCoverageTests</c> is the drift guard for the table list; this
///     is the behavioural half.
/// </summary>
public sealed class IntegrationConversationPurgeTests
{
    [Test]
    public async Task PurgingTheConversation_TakesTheSessionItsExecutionsAndTheirEvents()
    {
        using var fixture = new IntegrationTestFixture();
        var purgedConversationId = Guid.NewGuid();
        var survivingConversationId = Guid.NewGuid();

        await using (var context = await fixture.CreateSchemaAsync().ConfigureAwait(false))
        {
            var trigger = IntegrationTestFixture.Trigger();
            _ = context.IntegrationTriggers.Add(trigger);
            _ = context.IntegrationApiKeys.Add(IntegrationTestFixture.ApiKey());

            foreach (var conversationId in new[]
                     {
                         purgedConversationId,
                         survivingConversationId
                     })
            {
                _ = context.Conversations.Add(new NodeConversation
                {
                    ConversationId = conversationId,
                    CreatedAtUtc = 1,
                    LastSeenUtc = 1,
                    Kind = NodeConversationKind.Integration
                });

                var principal = Guid.NewGuid();
                var session = IntegrationTestFixture.Session(trigger.Id, principal, conversationId: conversationId);
                var execution = IntegrationTestFixture.Execution(trigger.Id, session.Id, principal);
                _ = context.IntegrationSessions.Add(session);
                _ = context.IntegrationExecutions.Add(execution);
                _ = context.IntegrationExecutionEvents.Add(IntegrationTestFixture.Event(execution.Id, sequence: 1, "execution.accepted"));
            }

            _ = await context.SaveChangesAsync().ConfigureAwait(false);

            await ConversationFootprintPurge.DeleteAsync(context, purgedConversationId, CancellationToken.None).ConfigureAwait(false);
        }

        // Raw COUNT(*), not an EF-graph assertion: the node connection runs without PRAGMA foreign_keys, so a
        // cascade-based delete would false-pass through the change tracker while leaving every child row on disk.
        AssertEx.Equal(expected: 1L, await fixture.RawTableCountAsync("integration_sessions").ConfigureAwait(false));
        AssertEx.Equal(expected: 1L, await fixture.RawTableCountAsync("integration_executions").ConfigureAwait(false));
        AssertEx.Equal(expected: 1L, await fixture.RawTableCountAsync("integration_execution_events").ConfigureAwait(false));

        var survivor = AssertEx.NotNull(await fixture.RawScalarAsync("SELECT conversation_id FROM integration_sessions;").ConfigureAwait(false));
        AssertEx.Equal(survivingConversationId,
            Guid.Parse(Convert.ToString(survivor, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture),
            "The sibling session must be untouched — the purge is keyed on one conversation.");

        // Node-scoped tables are correctly outside a conversation's footprint.
        AssertEx.Equal(expected: 1L, await fixture.RawTableCountAsync("integration_triggers").ConfigureAwait(false));
        AssertEx.Equal(expected: 1L, await fixture.RawTableCountAsync("integration_api_keys").ConfigureAwait(false));
    }
}
