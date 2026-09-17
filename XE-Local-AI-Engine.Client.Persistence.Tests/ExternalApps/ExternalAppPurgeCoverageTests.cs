namespace XE_Local_AI_Engine.Client.Persistence.Tests.ExternalApps;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

[Category(TestCategories.Integration)]
public sealed class ExternalAppPurgeCoverageTests
{
    /// <summary>
    ///     No <c>external_app_*</c> table may declare <c>conversation_id</c> or <c>message_id</c>. A chat purge deletes
    ///     every table keyed by those columns, so one such column would put an installed application's row — and with it
    ///     the only record of what is running on the machine — inside the blast radius of deleting a conversation.
    /// </summary>
    [Test]
    public async Task NoExternalAppTable_IsKeyedByAConversationOrMessage()
    {
        using var fixture = new ExternalAppTestFixture();
        await using var context = await fixture.CreateSchemaAsync();

        var offenders = new List<string>();
        var inspected = 0;
        foreach (var entityType in context.Model.GetEntityTypes())
        {
            var tableName = entityType.GetTableName();
            if (tableName is null || !tableName.StartsWith("external_app_", StringComparison.Ordinal))
            {
                continue;
            }

            inspected++;
            var storeObject = StoreObjectIdentifier.Table(tableName, entityType.GetSchema());
            if (entityType.GetProperties().Any(property => property.GetColumnName(storeObject) is "conversation_id" or "message_id"))
            {
                offenders.Add(tableName);
            }
        }

        AssertEx.Equal(expected: 2, inspected, "Both external_app_* tables must be discovered, or this assertion passes vacuously.");
        AssertEx.Empty(offenders,
            "An external_app_* table keyed by conversation_id/message_id would be deleted by the conversation footprint purge, "
            + $"leaving containers running with no row describing them: {string.Join(", ", offenders)}.");
    }
}
