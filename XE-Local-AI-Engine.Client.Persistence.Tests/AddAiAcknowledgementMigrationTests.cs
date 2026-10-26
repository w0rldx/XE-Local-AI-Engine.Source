namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     <c>AddAiAcknowledgement</c> adds the per-user AI responsibility acknowledgement on the IDENTITY context, nullable so
///     every existing user starts unacknowledged and sees the notice once.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class AddAiAcknowledgementMigrationTests
{
    [Test]
    public async Task Migrate_ToLatest_AddsANullableAcknowledgementToAspNetUsers()
    {
        await using var probe = await MigrationSchemaProbe.FromIdentityTemplateAsync("ai-acknowledgement.sqlite");
        await probe.ExecuteAsync("""
                                 INSERT INTO AspNetUsers (Id, AccessFailedCount, EmailConfirmed, LockoutEnabled, PhoneNumberConfirmed, TwoFactorEnabled, setup_completed, created_at_utc)
                                 VALUES ('u1', 0, 0, 0, 0, 0, 1, '2026-10-26 00:00:00');
                                 """);

        AssertEx.Equal(1L, await probe.ScalarAsync("SELECT COUNT(*) FROM AspNetUsers WHERE id = 'u1' AND ai_acknowledged_at_utc IS NULL;"),
            "A user created without the column set must read as not yet acknowledged.");
    }
}
