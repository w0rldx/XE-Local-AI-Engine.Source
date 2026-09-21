namespace XE_Local_AI_Engine.Tests.AgentHome;

using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The run list against real run directories: what it reads out of a well-formed run, what it does with a
///     malformed or oversized one, and what it refuses to carry off disk.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class AgentHomeRunListServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Planted in every file a run owns, so "no leakage" is asserted against a string that is really there.</summary>
    private const string Marker = "SECRET-MARKER-9f3a";

    private readonly TempDirectory _dataRoot = new("xe-run-list");
    private int _counter;

    public void Dispose() =>
        _dataRoot.Dispose();

    [Test]
    public async Task ListAsync_OrdersNewestFirstAndReportsTheUnpagedTotal()
    {
        var oldest = SeedRun(Now.AddDays(-3));
        var middle = SeedRun(Now.AddDays(-2));
        var newest = SeedRun(Now.AddDays(-1));

        var page = await Service().ListAsync(limit: 10, offset: 0);

        AssertEx.Equal(expected: 3, page.TotalCount);
        AssertEx.Equal(Path.GetFileName(newest), page.Items[0].RunId);
        AssertEx.Equal(Path.GetFileName(middle), page.Items[1].RunId);
        AssertEx.Equal(Path.GetFileName(oldest), page.Items[2].RunId);
    }

    [Test]
    public async Task ListAsync_PagesWithLimitAndOffsetWhileKeepingTheTotal()
    {
        SeedRun(Now.AddDays(-3));
        var middle = SeedRun(Now.AddDays(-2));
        SeedRun(Now.AddDays(-1));

        var page = await Service().ListAsync(limit: 1, offset: 1);

        AssertEx.Equal(expected: 1, page.Items.Count);
        AssertEx.Equal(Path.GetFileName(middle), page.Items[0].RunId, "offset counts from the newest run.");
        AssertEx.Equal(expected: 3, page.TotalCount, "the total ignores paging, or the client cannot draw a pager.");
    }

    [Test]
    public async Task ListAsync_WithNoRunsDirectory_AnswersAnEmptyPage()
    {
        var page = await Service().ListAsync(limit: 10, offset: 0);

        AssertEx.Empty(page.Items);
        AssertEx.Equal(expected: 0, page.TotalCount);
    }

    [Test]
    public async Task ListAsync_ReadsTheOutcomeConversationAndPatchFactsOffTheRunsOwnFiles()
    {
        var conversationId = Guid.NewGuid();
        var run = SeedRun(Now.AddDays(-1), conversationId: conversationId);
        WriteEvent(run, "run_completed", "status=Completed;changed_files=2;files_written=2");
        WritePatch(run, changedFiles: 2);

        var item = (await Service().ListAsync(limit: 10, offset: 0)).Items[0];

        AssertEx.Equal("Completed", item.Outcome);
        AssertEx.Equal(conversationId, item.ConversationId ?? Guid.Empty);
        AssertEx.True(item.PatchExported);
        AssertEx.Equal(expected: 2, item.ChangedFileCount ?? -1);
        AssertEx.Equal(AgentHomeRunApplyStates.None, item.ApplyState);
        AssertEx.True(item.SizeBytes > 0, "a run with files on disk has a size.");
    }

    [Test]
    public async Task ListAsync_WithNoPatch_ReportsNoExportAndNoFileCount()
    {
        var run = SeedRun(Now.AddDays(-1));
        WriteEvent(run, "run_completed", "status=Failed;changed_files=0;files_written=0");

        var item = (await Service().ListAsync(limit: 10, offset: 0)).Items[0];

        AssertEx.False(item.PatchExported);
        AssertEx.Null(item.ChangedFileCount, "a run with no export has no file count to report, which is not the same as zero.");
        AssertEx.Equal("Failed", item.Outcome);
    }

    [Test]
    [Arguments("patch_applied", "applied")]
    [Arguments("patch_apply_rejected", "rejected")]
    public async Task ListAsync_ReadsTheApplyStateFromTheRunsOwnApplyEvent(string eventName, string expected)
    {
        var run = SeedRun(Now.AddDays(-1));
        WriteEvent(run, "run_completed", "status=Completed");
        // The apply service writes the changed paths into this event's detail; only the NAME may be read.
        WriteEvent(run, eventName, $"repo-01/{Marker}.cs");
        WritePatch(run, changedFiles: 1);

        var item = (await Service().ListAsync(limit: 10, offset: 0)).Items[0];

        AssertEx.Equal(expected, item.ApplyState);
        AssertEx.False(Serialize(item).Contains(Marker, StringComparison.Ordinal),
            "the apply event's detail carries real paths; naming the event must not carry them with it.");
    }

    [Test]
    public async Task ListAsync_WithACancelledRun_ReportsCancelled()
    {
        var run = SeedRun(Now.AddDays(-1));
        WriteEvent(run, "cancelled", detail: null);

        var item = (await Service().ListAsync(limit: 10, offset: 0)).Items[0];

        AssertEx.Equal(AgentHomeRunOutcomes.Cancelled, item.Outcome);
    }

    /// <summary>
    ///     A status the node never emits is a status the list never repeats: the detail line is partly
    ///     model-influenced, so anything outside the closed set reads as unknown.
    /// </summary>
    [Test]
    [Arguments("status=NotAStatusTheNodeEmits", "a status outside the closed set")]
    [Arguments("files_written=3;status=Completed", "a detail whose status clause is not first")]
    [Arguments("", "an empty detail")]
    public async Task ListAsync_WithAnUnrecognizedCompletionDetail_ReportsUnknown(string detail, string why)
    {
        var run = SeedRun(Now.AddDays(-1));
        WriteEvent(run, "run_completed", detail);

        var item = (await Service().ListAsync(limit: 10, offset: 0)).Items[0];

        AssertEx.Equal(AgentHomeRunOutcomes.Unknown, item.Outcome, why);
    }

    [Test]
    public async Task ListAsync_WithAMalformedLog_StillReturnsTheRunAsUnknown()
    {
        var run = SeedRun(Now.AddDays(-1));
        await File.WriteAllTextAsync(EventsPath(run), "{not json at all\n[]\n\"a bare string\"\n{\"eventName\":42}\n");

        var page = await Service().ListAsync(limit: 10, offset: 0);

        AssertEx.Equal(expected: 1, page.Items.Count, "a malformed log must not remove the run from the page.");
        AssertEx.Equal(AgentHomeRunOutcomes.Unknown, page.Items[0].Outcome);
    }

    [Test]
    public async Task ListAsync_WithNoLogAtAll_StillReturnsTheRunAsUnknown()
    {
        var run = Path.Combine(RunsRoot(), RunId(Now.AddDays(-1), ++_counter));
        Directory.CreateDirectory(run);

        var page = await Service().ListAsync(limit: 10, offset: 0);

        AssertEx.Equal(expected: 1, page.Items.Count);
        AssertEx.Equal(AgentHomeRunOutcomes.Unknown, page.Items[0].Outcome);
        AssertEx.Null(page.Items[0].ConversationId);
    }

    /// <summary>
    ///     An events log far past the read caps, with the facts at both ends: the head window still finds the
    ///     conversation and the tail window still finds the outcome, without the middle ever being parsed.
    /// </summary>
    [Test]
    public async Task ListAsync_WithAnOversizedLog_StillReadsBothEndsWithoutReadingTheMiddle()
    {
        var conversationId = Guid.NewGuid();
        var run = SeedRun(Now.AddDays(-1), conversationId: conversationId);

        // One padding line per megabyte-ish, each carrying the marker: if the middle were parsed and echoed, the
        // no-leak assertion below would catch it.
        var padding = new string('p', 200000);
        await using (var writer = new StreamWriter(EventsPath(run), append: true))
        {
            for (var index = 0; index < 8; index++)
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(new { eventName = "noise", detail = $"{Marker}-{padding}" }));
            }
        }

        WriteEvent(run, "run_completed", "status=TimeBudgetExceeded");

        var item = (await Service().ListAsync(limit: 10, offset: 0)).Items[0];

        AssertEx.Equal("TimeBudgetExceeded", item.Outcome, "the tail window must still reach the last event.");
        AssertEx.Equal(conversationId, item.ConversationId ?? Guid.Empty, "the head window must still reach the started event.");
        AssertEx.False(Serialize(item).Contains(Marker, StringComparison.Ordinal));
    }

    [Test]
    public async Task ListAsync_WithAnOversizedChangedFilesRecord_ReportsNoCountRatherThanReadingIt()
    {
        var run = SeedRun(Now.AddDays(-1));
        Directory.CreateDirectory(Path.Combine(run, "patches"));
        await File.WriteAllTextAsync(Path.Combine(run, "patches", "changes.patch"), "diff --git a/x b/x\n");
        await File.WriteAllTextAsync(Path.Combine(run, "patches", "changed-files.json"),
            "[" + string.Join(separator: ',', Enumerable.Repeat($"\"{new string('z', 4096)}\"", count: 1200)) + "]");

        var item = (await Service().ListAsync(limit: 10, offset: 0)).Items[0];

        AssertEx.True(item.PatchExported);
        AssertEx.Null(item.ChangedFileCount, "a record past the size cap is not opened, so there is no count to report.");
    }

    /// <summary>
    ///     The no-leak property, asserted against a run whose EVERY file carries a marker: a host path, a patch body,
    ///     a command line and a model-authored file name. None of it may appear in the summary.
    /// </summary>
    [Test]
    public async Task ListAsync_CarriesNoPathPatchOrCommandContentOffDisk()
    {
        var run = SeedRun(Now.AddDays(-1));
        WriteEvent(run, "run_completed", $"status=Completed;note={Marker}");
        Directory.CreateDirectory(Path.Combine(run, "patches"));
        await File.WriteAllTextAsync(Path.Combine(run, "patches", "changes.patch"), $"diff --git a/{Marker} b/{Marker}\n+{Marker}\n");
        await File.WriteAllTextAsync(Path.Combine(run, "patches", "changed-files.json"),
            JsonSerializer.Serialize(new[] { new { alias = Marker, relativePath = $"src/{Marker}.cs", changeType = "modified" } }));
        await File.WriteAllTextAsync(Path.Combine(run, "logs", "commands.jsonl"),
            JsonSerializer.Serialize(new { executable = "git", arguments = new[] { Marker } }) + "\n");

        var item = (await Service().ListAsync(limit: 10, offset: 0)).Items[0];
        var serialized = Serialize(item);

        AssertEx.Equal(expected: 1, item.ChangedFileCount ?? -1, "the record was read, so the absence below is not for want of trying.");
        AssertEx.False(serialized.Contains(Marker, StringComparison.Ordinal),
            $"the summary carried content from the run's files: {serialized}");
        AssertEx.False(serialized.Contains(_dataRoot.Path, StringComparison.Ordinal),
            "no host path may appear in a run summary.");
    }

    /// <summary>
    ///     A run's log replaced by a link out of the run: the list must report the run as unknown and never open
    ///     what the link points at.
    /// </summary>
    [Test]
    public async Task ListAsync_WithALinkedEventLog_ReportsUnknownAndNeverFollowsIt()
    {
        SymlinkSupport.EnsureSupported();

        var outside = Path.Combine(_dataRoot.Path, "outside");
        Directory.CreateDirectory(outside);
        var target = Path.Combine(outside, "planted.jsonl");
        await File.WriteAllTextAsync(target,
            JsonSerializer.Serialize(new { eventName = "run_completed", detail = $"status=Completed;note={Marker}" }) + "\n");

        var run = SeedRun(Now.AddDays(-1));
        File.Delete(EventsPath(run));
        File.CreateSymbolicLink(EventsPath(run), target);

        var item = (await Service().ListAsync(limit: 10, offset: 0)).Items[0];

        AssertEx.Equal(AgentHomeRunOutcomes.Unknown, item.Outcome,
            "a linked log is refused, so the outcome it claims never reaches the page.");
        AssertEx.False(Serialize(item).Contains(Marker, StringComparison.Ordinal));
        AssertEx.True(File.Exists(target), "the link's target is read-only collateral; nothing here may touch it.");
    }

    /// <summary>The blocking case: a FIFO named <c>events.jsonl</c>, which an open would wait on forever.</summary>
    /// <remarks>
    ///     .NET exposes no file-type signal before the open — a FIFO stats as a normal, zero-length, non-link file —
    ///     and the open itself never returns, so the refusal has to come from the stat. The budget below IS the
    ///     assertion: a list that blocks fails it rather than hanging the suite.
    /// </remarks>
    [Test]
    public async Task ListAsync_WithAFifoInPlaceOfTheEventLog_ReturnsPromptlyAndLeavesTheOtherRunsIntact()
    {
        var blocking = SeedRun(Now.AddDays(-1));
        File.Delete(EventsPath(blocking));
        if (!TryCreateFifo(EventsPath(blocking)))
        {
            Skip.Test("BLOCKED: this host has no usable mkfifo, so the blocking-open case cannot be built here.");
            return;
        }

        var healthy = SeedRun(Now.AddDays(-2));
        WriteEvent(healthy, "run_completed", "status=Completed");

        var list = Service().ListAsync(limit: 10, offset: 0);
        // A deadline the test owns, not a sleep: the list either answers inside it or the guard is not there.
        var finished = await Task.WhenAny(list, Task.Delay(TestBudgets.Contended));
        AssertEx.True(ReferenceEquals(finished, list),
            "the list opened a FIFO and blocked; the refusal must happen from the stat, before any open.");

        var page = await list;
        AssertEx.Equal(expected: 2, page.TotalCount);
        AssertEx.Equal(AgentHomeRunOutcomes.Unknown, page.Items[0].Outcome, "the blocking run is reported, as unknown.");
        AssertEx.Equal("Completed", page.Items[1].Outcome, "one unreadable run must not cost the page its other rows.");
    }

    [Test]
    public async Task ListAsync_SkipsADirectoryWhoseNameTheNodeDidNotMint()
    {
        Directory.CreateDirectory(Path.Combine(RunsRoot(), "not-a-run"));
        Directory.CreateDirectory(Path.Combine(RunsRoot(), "run-notanumber-1"));
        SeedRun(Now.AddDays(-1));

        var page = await Service().ListAsync(limit: 10, offset: 0);

        AssertEx.Equal(expected: 1, page.TotalCount, "only directories the node minted are runs.");
    }

    private AgentHomeRunListService Service() =>
        new(Options.Create(new AgentHomeOptions { Enabled = true, RootPath = Path.Combine(_dataRoot.Path, "agent-home-state") }),
            new FakeNodeDataDirectory(_dataRoot.Path));

    private static string Serialize(AgentHomeRunSummary summary) =>
        JsonSerializer.Serialize(summary);

    private string RunsRoot()
    {
        var root = Path.Combine(_dataRoot.Path, "agent-home-state", "agent-home", "runs");
        Directory.CreateDirectory(root);
        return root;
    }

    private static string RunId(DateTimeOffset startedAt, int counter) =>
        string.Create(CultureInfo.InvariantCulture, $"run-{startedAt.ToUnixTimeMilliseconds()}-{counter}");

    private static string EventsPath(string runDirectory) =>
        Path.Combine(runDirectory, "logs", "events.jsonl");

    private string SeedRun(DateTimeOffset startedAt, Guid? conversationId = null)
    {
        var directory = Path.Combine(RunsRoot(), RunId(startedAt, ++_counter));
        Directory.CreateDirectory(Path.Combine(directory, "logs"));
        File.WriteAllText(EventsPath(directory),
            JsonSerializer.Serialize(new
            {
                eventName = "started",
                runId = Path.GetFileName(directory),
                conversationId
            }) + "\n");
        return directory;
    }

    /// <summary>Makes <paramref name="path" /> a FIFO, or reports that this host cannot.</summary>
    private static bool TryCreateFifo(string path)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("mkfifo")
            {
                ArgumentList = { path },
                RedirectStandardError = true
            });
            if (process is null)
            {
                return false;
            }

            process.WaitForExit(milliseconds: 10000);
            return process.HasExited && process.ExitCode == 0 && File.Exists(path);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static void WriteEvent(string runDirectory, string eventName, string? detail) =>
        File.AppendAllText(EventsPath(runDirectory), JsonSerializer.Serialize(new { eventName, detail }) + "\n");

    private static void WritePatch(string runDirectory, int changedFiles)
    {
        var patches = Path.Combine(runDirectory, "patches");
        Directory.CreateDirectory(patches);
        File.WriteAllText(Path.Combine(patches, "changes.patch"), "diff --git a/x b/x\n");
        File.WriteAllText(Path.Combine(patches, "changed-files.json"),
            JsonSerializer.Serialize(Enumerable.Range(start: 0, changedFiles)
                                               .Select(index => new { alias = "repo-01", relativePath = $"src/File{index}.cs" })));
    }
}
