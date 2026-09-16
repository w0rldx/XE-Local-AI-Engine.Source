namespace XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     The failure half of <see cref="MigrationSchemaProbe" />'s construction. Every successful path is covered by the
///     suites that use a probe; this is the path that returns no probe at all, so nothing else is left holding the
///     directory and the key holder it had already created.
/// </summary>
public sealed class MigrationSchemaProbeTests
{
    [Test]
    public async Task CreateAsync_WhenTheDatabaseStepThrows_LeavesNoPrivateDirectoryBehind()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"xe-local-ai-engine-probe-failed-{Guid.NewGuid():N}");
        var undeclared = Guid.NewGuid().ToString("N");

        var failure = await AssertEx.ThrowsAsync<InvalidOperationException>(async () =>
                                    {
                                        await using var probe = await MigrationSchemaProbe
                                                                      .CreateAsync(rootPath, "undeclared.sqlite",
                                                                          path => MigratedDatabaseTemplate.CopyChatAtAsync(path, undeclared))
                                                                      .ConfigureAwait(false);
                                    })
                                    .ConfigureAwait(false);

        AssertEx.True(failure.Message.Contains(undeclared, StringComparison.Ordinal),
            "The cleanup must not swallow or replace the failure that caused it.");
        AssertEx.False(Directory.Exists(rootPath),
            "A probe that was never returned must take its private directory with it — no DisposeAsync will ever run for it.");
    }
}
