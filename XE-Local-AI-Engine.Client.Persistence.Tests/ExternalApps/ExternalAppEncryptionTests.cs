namespace XE_Local_AI_Engine.Client.Persistence.Tests.ExternalApps;

using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     What the instance's variables column actually protects: the bytes at rest, the AAD binding that stops one
///     instance's sealed values being read back as another's, and the two records that carry those values decrypted and
///     therefore must print nothing.
/// </summary>
public sealed class ExternalAppEncryptionTests
{
    [Test]
    public async Task VariablesJson_WhenSaved_IsNotPlaintextAtRest()
    {
        using var fixture = new ExternalAppTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var command = ExternalAppTestFixture.Create();
        _ = await new ExternalAppInstanceStore(context).CreateAsync(command).ConfigureAwait(false);

        var stored = AssertEx.NotNull(await fixture.RawScalarAsync("SELECT variables_json FROM external_app_instances WHERE id = $id;",
                                                       sqlCommand => sqlCommand.Parameters.AddWithValue("$id", command.Id))
                                                   .ConfigureAwait(false)) as byte[];

        AssertEx.False(AssertEx.NotNull(stored).AsSpan().IndexOf(Encoding.UTF8.GetBytes(ExternalAppTestFixture.SeedSecret)) >= 0,
            "An application's admin password must not survive as plaintext in the database file.");
    }

    [Test]
    public async Task VariablesJson_RoundTripsThroughAFreshContext()
    {
        using var fixture = new ExternalAppTestFixture();
        var command = ExternalAppTestFixture.Create();

        await using (var writeContext = await fixture.CreateSchemaAsync().ConfigureAwait(false))
        {
            _ = await new ExternalAppInstanceStore(writeContext).CreateAsync(command).ConfigureAwait(false);
        }

        // A fresh context, so the answer comes from the materialization interceptor rather than from the writer's own
        // change tracker, which still holds the plaintext.
        await using var readContext = fixture.CreateContext();
        var snapshot = AssertEx.NotNull(await new ExternalAppInstanceStore(readContext).GetAsync(command.Id).ConfigureAwait(false));

        AssertEx.Equal(ExternalAppTestFixture.SeedVariablesJson, snapshot.VariablesJson, "The store boundary hands the application layer decrypted text, never the sealed bytes.");
    }

    [Test]
    public async Task VariablesJson_WhenTheRowIsReparented_FailsItsTagCheck()
    {
        using var fixture = new ExternalAppTestFixture();
        var victim = ExternalAppTestFixture.Create();
        var attacker = ExternalAppTestFixture.Create(applicationId: "searxng", variablesJson: """{"SEARXNG_SECRET":"unrelated"}""");

        await using (var context = await fixture.CreateSchemaAsync().ConfigureAwait(false))
        {
            var store = new ExternalAppInstanceStore(context);
            _ = await store.CreateAsync(victim).ConfigureAwait(false);
            _ = await store.CreateAsync(attacker).ConfigureAwait(false);
        }

        var stored = AssertEx.NotNull(await fixture.RawScalarAsync("SELECT variables_json FROM external_app_instances WHERE id = $id;",
                                                       sqlCommand => sqlCommand.Parameters.AddWithValue("$id", victim.Id))
                                                   .ConfigureAwait(false)) as byte[];

        // Copy one instance's sealed variables onto another's row. The AAD binds the row's own id in BOTH slots, so the
        // copy must fail authentication rather than hand the second instance the first one's credentials.
        await fixture.RawExecuteAsync("UPDATE external_app_instances SET variables_json = $payload WHERE id = $id;",
                         sqlCommand =>
                         {
                             sqlCommand.Parameters.AddWithValue("$payload", stored!);
                             sqlCommand.Parameters.AddWithValue("$id", attacker.Id);
                         })
                     .ConfigureAwait(false);

        await using var attackContext = fixture.CreateContext();
        _ = AssertEx.Throws<CryptographicException>(() => _ = attackContext.ExternalAppInstances.AsNoTracking().SingleOrDefault(row => row.Id == attacker.Id),
            "A re-parented variables column must fail its tag check instead of reading back as another instance's secrets.");
    }

    [Test]
    public async Task EventDetailJson_IsStoredAsPlaintextOnPurpose()
    {
        // The fence for a deliberate decision: the event feed is content-free by contract and is replayed to the hub as
        // it stands, so encrypting it would buy nothing and cost the replay. If a later change puts real content here,
        // this test is the one that has to be re-argued.
        using var fixture = new ExternalAppTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = new ExternalAppInstanceStore(context);
        var command = ExternalAppTestFixture.Create(firstEventDetailJson: """{"acceptedPermissions":["internet"]}""");
        _ = await store.CreateAsync(command).ConfigureAwait(false);

        var stored = await fixture.RawScalarAsync("SELECT detail_json FROM external_app_instance_events WHERE instance_id = $id;",
                                       sqlCommand => sqlCommand.Parameters.AddWithValue("$id", command.Id))
                                  .ConfigureAwait(false);

        AssertEx.Equal("""{"acceptedPermissions":["internet"]}""", AssertEx.NotNull(stored as string), "detail_json is a TEXT column with no interceptor entry, by design.");
    }

    [Test]
    public void Snapshot_ToString_DoesNotContainTheSecret()
    {
        var snapshot = new ExternalAppInstanceSnapshot(Guid.NewGuid(),
            "odysseus",
            ManifestVersion: 1,
            ExternalAppTestFixture.SeedManifestJson,
            "Odysseus",
            ExternalAppInstanceStatus.Running,
            ExternalAppDesiredState.Running,
            RuntimeOverride: null,
            "docker",
            ExternalAppTestFixture.SeedVariablesJson,
            "{}",
            "/var/lib/xe/external-apps/instances/0",
            FailureCategory: null,
            FailureSummary: null,
            NeedsRecreate: false,
            InstalledAtUtc: 1_000,
            StartedAtUtc: 2_000,
            StoppedAtUtc: null,
            UpdatedAtUtc: 2_000,
            LastSequence: 2,
            Version: 1);

        // One LogDebug("{Snapshot}", snapshot) is all it would take, so the printer is suppressed rather than merely
        // forbidden — and the type name still has to survive, or a suppressed printer would be indistinguishable from
        // a broken ToString.
        var printed = snapshot.ToString();
        AssertEx.False(printed.Contains(ExternalAppTestFixture.SeedSecret, StringComparison.Ordinal), "A snapshot carries DECRYPTED variables and must print none of them.");
        AssertEx.True(printed.Contains(nameof(ExternalAppInstanceSnapshot), StringComparison.Ordinal));
    }

    [Test]
    public void Create_ToString_DoesNotContainTheSecret()
    {
        var command = ExternalAppTestFixture.Create();

        var printed = command.ToString();
        AssertEx.False(printed.Contains(ExternalAppTestFixture.SeedSecret, StringComparison.Ordinal), "The install command carries the same plaintext values on the way in.");
        AssertEx.True(printed.Contains(nameof(ExternalAppInstanceCreate), StringComparison.Ordinal));
    }
}
