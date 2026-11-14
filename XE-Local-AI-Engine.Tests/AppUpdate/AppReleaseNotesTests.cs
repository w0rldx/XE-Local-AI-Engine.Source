namespace XE_Local_AI_Engine.Tests.AppUpdate;

using System.Text.Json;
using Velopack;
using XE_Local_AI_Engine.Client.Endpoints.AppUpdate.V1;
using XE_Local_AI_Engine.Client.Services.AppUpdate;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The What's New contract: which feed entries become release notes, how they are ordered against the running
///     version, the size cap, and the wire shape of <c>GET app-update/release-notes</c>.
/// </summary>
/// <remarks>
///     The route maps only on a desktop host and the test host is headless, so the endpoint's mapper is driven
///     directly, as <see cref="ApplyAppUpdateEndpointTests" /> does.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed class AppReleaseNotesTests
{
    [Test]
    public void ReleaseNotes_KeepsOneNotePerVersionFromFullPackagesThatCarryNotes()
    {
        var notes = AppUpdateVersions.ReleaseNotes([
            Asset("1.0.0", VelopackAssetType.Delta, "delta notes"),
            Asset("1.0.0", VelopackAssetType.Full, "full notes"),
            Asset("1.0.1", VelopackAssetType.Full, "   "),
            Asset("1.0.2", VelopackAssetType.Full, notes: null)
        ]);

        AssertEx.Equal(1, notes.Count);
        var note = notes[0];
        AssertEx.Equal("1.0.0", note.Version);
        AssertEx.Equal("full notes", note.NotesMarkdown);
        AssertEx.False(note.IsTruncated);
    }

    [Test]
    public void ReleaseNotes_CapsAnOversizedBodyAndFlagsIt()
    {
        var notes = AppUpdateVersions.ReleaseNotes([
            Asset("1.0.0", VelopackAssetType.Full, new string('a', AppUpdateVersions.MaxNotesLength + 1))
        ]);

        AssertEx.Equal(1, notes.Count);
        var note = notes[0];
        AssertEx.Equal(AppUpdateVersions.MaxNotesLength, note.NotesMarkdown.Length);
        AssertEx.True(note.IsTruncated);
    }

    [Test]
    public void ReleaseNotes_NeverCutsASurrogatePairInHalf()
    {
        // The emoji's high surrogate sits exactly at the last kept index.
        var body = new string('a', AppUpdateVersions.MaxNotesLength - 1) + "\U0001F600" + "tail";

        var notes = AppUpdateVersions.ReleaseNotes([Asset("1.0.0", VelopackAssetType.Full, body)]);

        AssertEx.Equal(1, notes.Count);
        var note = notes[0];
        AssertEx.Equal(AppUpdateVersions.MaxNotesLength - 1, note.NotesMarkdown.Length);
        AssertEx.False(char.IsHighSurrogate(note.NotesMarkdown[^1]));
        AssertEx.True(note.IsTruncated);
    }

    [Test]
    public void SelectReleaseNotes_PutsTheRunningVersionFirst_ThenNewerOnesNewestFirst_AndDropsOlderOnes()
    {
        var selected = AppUpdateVersions.SelectReleaseNotes([
            Note("1.0.0-rc.1"),
            Note("1.0.0-rc.3"),
            Note("1.0.0-rc.2"),
            Note("1.0.0-rc.2.dev.20261105.1")
        ], currentVersion: "1.0.0-rc.2");

        AssertEx.Equal("1.0.0-rc.2:False,1.0.0-rc.3:True,1.0.0-rc.2.dev.20261105.1:True", Describe(selected));
    }

    [Test]
    public void SelectReleaseNotes_KeepsAtMostThree_AndCountsAVersionFromTwoFeedsOnce()
    {
        var selected = AppUpdateVersions.SelectReleaseNotes([
            Note("1.0.1"),
            Note("1.0.2"),
            Note("1.0.2"),
            Note("1.0.3"),
            Note("1.0.4")
        ], currentVersion: "1.0.0");

        AssertEx.Equal(AppUpdateVersions.MaxReleaseNotes, selected.Count);
        AssertEx.Equal("1.0.4:True,1.0.3:True,1.0.2:True", Describe(selected));
    }

    [Test]
    public void SelectReleaseNotes_WhenTheRunningVersionIsNotOnTheFeed_ShowsOnlyNewerOnes()
    {
        // The feed holds the newest releases only, so a node several builds behind has no note for its own version.
        var selected = AppUpdateVersions.SelectReleaseNotes([Note("1.0.2"), Note("1.0.3")], currentVersion: "1.0.0");

        AssertEx.Equal("1.0.3:True,1.0.2:True", Describe(selected));
    }

    [Test]
    public void SelectReleaseNotes_WithAnUnparseableRunningVersion_SelectsNothing()
    {
        AssertEx.Empty(AppUpdateVersions.SelectReleaseNotes([Note("1.0.0")], "not-a-version"));
    }

    [Test]
    public void Endpoint_BeforeTheFirstCheck_ReturnsAnEmptyList()
    {
        var response = GetAppReleaseNotesEndpoint.ToResponse(AppUpdateSnapshot.Empty);

        AssertEx.Equal("0.0.0", response.CurrentVersion);
        AssertEx.Empty(response.Items);
    }

    [Test]
    public void Endpoint_MapsEveryNoteField_UnderTheCamelCaseNamesTheSpaReads()
    {
        var snapshot = AppUpdateSnapshot.Empty with
        {
            CurrentVersion = "1.0.0",
            ReleaseNotes =
            [
                Note("1.0.0") with
                {
                    NotesMarkdown = "## 1.0.0",
                    IsTruncated = true
                },
                Note("1.0.1") with
                {
                    IsNewerThanRunning = true
                }
            ]
        };

        var response = GetAppReleaseNotesEndpoint.ToResponse(snapshot);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(response, JsonSerializerOptions.Web));
        var root = json.RootElement;
        AssertEx.Equal("1.0.0", root.GetProperty("currentVersion").GetString());
        var first = root.GetProperty("items")[0];
        AssertEx.Equal("1.0.0", first.GetProperty("version").GetString());
        AssertEx.Equal("## 1.0.0", first.GetProperty("notesMarkdown").GetString());
        AssertEx.True(first.GetProperty("isTruncated").GetBoolean());
        AssertEx.False(first.GetProperty("isNewerThanRunning").GetBoolean());
        AssertEx.True(root.GetProperty("items")[1].GetProperty("isNewerThanRunning").GetBoolean());
    }

    private static string Describe(IEnumerable<AppReleaseNote> notes) =>
        string.Join(',', notes.Select(static note => $"{note.Version}:{note.IsNewerThanRunning}"));

    private static AppReleaseNote Note(string version) =>
        new()
        {
            Version = version,
            NotesMarkdown = $"notes for {version}"
        };

    private static VelopackAsset Asset(string version, VelopackAssetType type, string? notes)
    {
        return new VelopackAsset
        {
            PackageId = "XE-Local-AI-Engine",
            Version = SemanticVersion.Parse(version),
            Type = type,
            FileName = $"XE-Local-AI-Engine-{version}-{type}.nupkg",
            SHA1 = "a",
            SHA256 = "b",
            Size = 1,
            NotesMarkdown = notes!
        };
    }
}
