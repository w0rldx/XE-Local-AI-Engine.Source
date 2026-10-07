namespace XE_Local_AI_Engine.Tests.Sandbox.Mxc;

using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Mxc;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary><see cref="MxcGrantJournal" />: what a launch recorded is what the next startup sweep drains, once.</summary>
[Category(TestCategories.Unit)]
public sealed class MxcGrantJournalTests
{
    private static readonly string[] OwnedRoots = ["/jail", "/venv"];

    [Test]
    public async Task RecordThenDrain_ReturnsEveryPathOnce_AndEmptiesTheJournal()
    {
        var directory = Directory.CreateTempSubdirectory("xe-mxc-journal-").FullName;
        try
        {
            var journal = Path.Combine(directory, "nested", "mxc-grants.journal");
            MxcGrantJournal.Record(journal, ["/jail/a", "/venv/.venv"], OwnedRoots, NullLogger.Instance);
            MxcGrantJournal.Record(journal, ["/jail/b", "/venv/.venv", " "], OwnedRoots, NullLogger.Instance);

            var drained = MxcGrantJournal.Drain(journal, OwnedRoots, NullLogger.Instance);

            AssertEx.Equal("/jail/a|/venv/.venv|/jail/b", string.Join('|', drained));
            AssertEx.False(File.Exists(journal));
            AssertEx.Empty(MxcGrantJournal.Drain(journal, OwnedRoots, NullLogger.Instance));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        await Task.CompletedTask;
    }

    [Test]
    public async Task OperatorSuppliedTrees_AreNeitherRecordedNorDrainedForTheSweep()
    {
        // An operator's MCP server directory may carry another application's legitimate AppContainer ACEs; the sweep must never see it.
        var directory = Directory.CreateTempSubdirectory("xe-mxc-journal-").FullName;
        try
        {
            var journal = Path.Combine(directory, "mxc-grants.journal");
            MxcGrantJournal.Record(journal, ["/jail/a", "/opt/mcp-server", "/jail"], OwnedRoots, NullLogger.Instance);
            // A journal written before the scope was narrowed still holds the operator tree; draining drops it.
            await File.AppendAllLinesAsync(journal, ["/opt/other-mcp-server", "/jail-sibling/x"]);

            var drained = MxcGrantJournal.Drain(journal, OwnedRoots, NullLogger.Instance);

            AssertEx.Equal("/jail/a", string.Join('|', drained));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
