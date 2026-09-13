namespace XE_Local_AI_Engine.Tests.ExternalApps;

using System.Text;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.ExternalApps;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The no-follow rules and the reuse-or-replace materialisation. Both exist because lexical confinement is not
///     confinement and because start, update and reset all re-enter the same pass: "a pre-existing file is a
///     violation" would fail every rebuild after the first.
/// </summary>
public sealed class ExternalAppStorageLayoutTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "xe-external-apps-layout-" + Guid.NewGuid().ToString("N"));
    private readonly Guid _instanceId = Guid.NewGuid();

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A leftover temp tree is not worth failing a green run over.
        }
    }

    [Test]
    public void Prepare_CreatesOneVolumeDirectoryPerServiceAndStorageName()
    {
        var layout = CreateLayout();
        var manifest = ExternalAppTestManifests.Manifest([
            ExternalAppTestManifests.Service("app", storage: [new ApplicationStorage("data", "/data")]),
            ExternalAppTestManifests.Service("db", storage: [new ApplicationStorage("data", "/var/lib/data")], image: ExternalAppTestManifests.SecondImage)
        ]);

        var paths = layout.Prepare(_instanceId, manifest);

        var app = paths.VolumePath("app", "data");
        var db = paths.VolumePath("db", "data");
        AssertEx.True(Directory.Exists(app), "The app service's data directory must exist.");
        AssertEx.True(Directory.Exists(db), "The db service's data directory must exist.");
        AssertEx.NotEqual(app, db);
    }

    [Test]
    public void Prepare_OnLinux_NarrowsCreatedDirectoriesToOwnerOnly()
    {
        var layout = CreateLayout();
        var manifest = ExternalAppTestManifests.Manifest([ExternalAppTestManifests.Service("app", storage: [new ApplicationStorage("data", "/data")])]);

        var paths = layout.Prepare(_instanceId, manifest);

        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            AssertEx.True(Directory.Exists(paths.InstanceRoot), "Windows has no Unix mode; the directory must still be created.");
            return;
        }

        var mode = File.GetUnixFileMode(paths.VolumePath("app", "data"));
        AssertEx.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, mode);
    }

    [Test]
    public void Prepare_WhenAVolumeComponentIsASymlink_FailsAndWritesNothingBeneathIt()
    {
        SymlinkSupport.EnsureSupported();

        var layout = CreateLayout();
        var paths = layout.Describe(_instanceId);
        var elsewhere = Path.Combine(_root, "elsewhere");
        _ = Directory.CreateDirectory(elsewhere);
        _ = Directory.CreateDirectory(paths.VolumesRoot);
        Directory.CreateSymbolicLink(Path.Combine(paths.VolumesRoot, "app"), elsewhere);

        var manifest = ExternalAppTestManifests.Manifest([ExternalAppTestManifests.Service("app", storage: [new ApplicationStorage("data", "/data")])]);

        _ = AssertEx.Throws<ExternalAppStorageException>(() => layout.Prepare(_instanceId, manifest));
        AssertEx.False(Directory.Exists(Path.Combine(elsewhere, "data")), "A refused path must not have been created through the link.");
    }

    [Test]
    public void Prepare_WhenTheFilesParentIsASymlink_Fails()
    {
        SymlinkSupport.EnsureSupported();

        var layout = CreateLayout();
        var paths = layout.Describe(_instanceId);
        var elsewhere = Path.Combine(_root, "elsewhere-files");
        _ = Directory.CreateDirectory(elsewhere);
        _ = Directory.CreateDirectory(paths.InstanceRoot);
        Directory.CreateSymbolicLink(paths.FilesRoot, elsewhere);

        var manifest = ManifestWithAsset("settings", "hello");

        _ = AssertEx.Throws<ExternalAppStorageException>(() => layout.Prepare(_instanceId, manifest));
        AssertEx.Empty(Directory.GetFiles(elsewhere, "*", SearchOption.AllDirectories));
    }

    [Test]
    public void Prepare_WhenAFileSitsWhereADirectoryBelongs_Fails()
    {
        var layout = CreateLayout();
        var paths = layout.Describe(_instanceId);
        _ = Directory.CreateDirectory(paths.VolumesRoot);
        File.WriteAllText(Path.Combine(paths.VolumesRoot, "app"), "not a directory");

        var manifest = ExternalAppTestManifests.Manifest([ExternalAppTestManifests.Service("app", storage: [new ApplicationStorage("data", "/data")])]);

        _ = AssertEx.Throws<ExternalAppStorageException>(() => layout.Prepare(_instanceId, manifest));
    }

    [Test]
    [Arguments("../escape.yml")]
    [Arguments("conf/../../escape.yml")]
    [Arguments("/etc/passwd")]
    public void Prepare_WithATraversingOrRootedAssetSource_FailsBeforeAnyByteIsWritten(string source)
    {
        var layout = CreateLayout();
        var asset = ExternalAppTestManifests.File(source, "/app/settings.yml", "body");
        var manifest = ExternalAppTestManifests.Manifest([ExternalAppTestManifests.Service("app", files: [asset])]);

        _ = AssertEx.Throws<ExternalAppStorageException>(() => layout.Prepare(_instanceId, manifest));
        AssertEx.Empty(Directory.Exists(_root) ? Directory.GetFiles(_root, "*", SearchOption.AllDirectories) : []);
    }

    [Test]
    public void Prepare_WhenTheAssetHashDisagreesWithItsContent_Fails()
    {
        var layout = CreateLayout();
        var bytes = Encoding.UTF8.GetBytes("body");
        var lying = new ApplicationFile("settings.yml",
            "/app/settings.yml",
            "0000000000000000000000000000000000000000000000000000000000000000",
            Convert.ToBase64String(bytes));
        var manifest = ExternalAppTestManifests.Manifest([ExternalAppTestManifests.Service("app", files: [lying])]);

        _ = AssertEx.Throws<ExternalAppStorageException>(() => layout.Prepare(_instanceId, manifest));
    }

    [Test]
    public void Materialize_WritesTheAssetAndItsNestedDirectories()
    {
        var layout = CreateLayout();
        var manifest = ManifestWithAsset("files/app/settings.yml", "server: on");

        var paths = layout.Prepare(_instanceId, manifest);

        var target = paths.FilePath("app", Path.Combine("files", "app", "settings.yml"));
        AssertEx.Equal("server: on", File.ReadAllText(target));
    }

    /// <summary>
    ///     The reuse branch: a start after a configure, an update and a reset all re-enter the pass, and rewriting an
    ///     identical file on every one of them would churn the mount for nothing. Proven by the write time, because
    ///     "the content is the same" is true either way.
    /// </summary>
    [Test]
    public void Materialize_WhenTheFileExistsWithTheRecordedHash_ReusesItAndDoesNotRewrite()
    {
        var layout = CreateLayout();
        var manifest = ManifestWithAsset("settings.yml", "server: on");
        var paths = layout.Prepare(_instanceId, manifest);
        var target = paths.FilePath("app", "settings.yml");

        var stamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(target, stamp);

        _ = layout.Prepare(_instanceId, manifest);

        AssertEx.Equal(stamp, File.GetLastWriteTimeUtc(target));
    }

    /// <summary>
    ///     The reuse branch hands the target out as a read-only bind SOURCE, so hashing correctly is not enough: a
    ///     symlink hashes to whatever it points at, and reusing one would mount a file from outside the instance
    ///     directory into the container. The leaf is checked before the hash is even read.
    /// </summary>
    [Test]
    public void Materialize_WhenTheExistingFileIsASymlinkThatHashesCorrectly_IsRefused()
    {
        SymlinkSupport.EnsureSupported();

        var layout = CreateLayout();
        var manifest = ManifestWithAsset("settings.yml", "server: on");
        var paths = layout.Prepare(_instanceId, manifest);
        var target = paths.FilePath("app", "settings.yml");

        // The link's own target carries the very bytes the manifest declares, so every hash check passes.
        var elsewhere = Path.Combine(_root, "outside-the-instance.yml");
        File.WriteAllText(elsewhere, "server: on");
        File.Delete(target);
        File.CreateSymbolicLink(target, elsewhere);

        var failure = AssertEx.Throws<ExternalAppStorageException>(() => layout.Prepare(_instanceId, manifest));

        AssertEx.Contains(failure.Message, "is a link");
        AssertEx.True(new FileInfo(target).LinkTarget is not null, "The refusal must leave the planted link alone rather than quietly replacing it.");
    }

    [Test]
    public void Materialize_WhenTheFileExistsWithADifferentHash_ReplacesItAndLeavesNoTempFile()
    {
        var layout = CreateLayout();
        var manifest = ManifestWithAsset("settings.yml", "server: on");
        var paths = layout.Prepare(_instanceId, manifest);
        var target = paths.FilePath("app", "settings.yml");
        File.WriteAllText(target, "tampered");

        _ = layout.Prepare(_instanceId, manifest);

        AssertEx.Equal("server: on", File.ReadAllText(target));
        AssertEx.Empty(Directory.GetFiles(Path.GetDirectoryName(target)!, "*.tmp-*"));
    }

    [Test]
    public void Materialize_WhenTheRenameFails_LeavesNoTempFileBehind()
    {
        var layout = CreateLayout();
        var manifest = ManifestWithAsset("settings.yml", "server: on");
        var paths = layout.Describe(_instanceId);
        var target = paths.FilePath("app", "settings.yml");
        _ = Directory.CreateDirectory(target);

        _ = AssertEx.Throws<ExternalAppStorageException>(() => layout.Prepare(_instanceId, manifest));

        AssertEx.Empty(Directory.GetFiles(Path.GetDirectoryName(target)!, "*.tmp-*"));
    }

    /// <summary>Reset wipes the writable volumes and nothing else; the assets survive and are re-verified, not trusted.</summary>
    [Test]
    public void DeleteVolumes_RemovesTheWritableTreeAndKeepsTheAssets()
    {
        var layout = CreateLayout();
        var manifest = ExternalAppTestManifests.Manifest([
            ExternalAppTestManifests.Service("app",
                storage: [new ApplicationStorage("data", "/data")],
                files: [ExternalAppTestManifests.File("settings.yml", "/app/settings.yml", "server: on")])
        ]);
        var paths = layout.Prepare(_instanceId, manifest);
        File.WriteAllText(Path.Combine(paths.VolumePath("app", "data"), "state.db"), "rows");

        layout.DeleteVolumes(_instanceId);

        AssertEx.False(Directory.Exists(paths.VolumesRoot), "Reset must remove the writable tree.");
        AssertEx.True(File.Exists(paths.FilePath("app", "settings.yml")), "Reset must keep the catalog assets.");

        _ = layout.Prepare(_instanceId, manifest);
        AssertEx.True(Directory.Exists(paths.VolumePath("app", "data")), "The re-entered pass must recreate the volume.");
    }

    [Test]
    public void Delete_RemovesTheWholeInstanceDirectoryAndIsIdempotent()
    {
        var layout = CreateLayout();
        var manifest = ManifestWithAsset("settings.yml", "server: on");
        var paths = layout.Prepare(_instanceId, manifest);

        AssertEx.True(layout.Delete(_instanceId), "The first delete must succeed.");
        AssertEx.False(Directory.Exists(paths.InstanceRoot), "The instance directory must be gone.");
        AssertEx.True(layout.Delete(_instanceId), "Deleting what is already gone is a success, not a failure.");
    }

    /// <summary>
    ///     The delete is recursive, so the path it walks is as load-bearing as the one a write walks. A directory link
    ///     at any ancestor inside the feature root sends it out of the tree this feature owns, and the answer is a
    ///     refusal rather than a "best-effort" false — the caller keeps the row and the user keeps the data.
    /// </summary>
    [Test]
    public void Delete_WhenAnAncestorInsideTheFeatureRootIsASymlink_RefusesAndDeletesNothingThroughIt()
    {
        SymlinkSupport.EnsureSupported();

        var layout = CreateLayout();

        // 'instances' is the link, so the instance directory the delete is aimed at exists — through it.
        var elsewhere = Path.Combine(_root, "elsewhere-instances");
        _ = Directory.CreateDirectory(Path.Combine(elsewhere, _instanceId.ToString("N")));
        var bystander = Path.Combine(elsewhere, _instanceId.ToString("N"), "someone-elses-data");
        File.WriteAllText(bystander, "rows");
        _ = Directory.CreateDirectory(Path.Combine(_root, "external-apps"));
        Directory.CreateSymbolicLink(Path.Combine(_root, "external-apps", "instances"), elsewhere);

        AssertEx.True(Directory.Exists(layout.Describe(_instanceId).InstanceRoot),
            "The delete must have a directory to aim at through the link, or its refusal proves nothing.");

        var failure = AssertEx.Throws<ExternalAppStorageException>(() => layout.Delete(_instanceId));

        AssertEx.Contains(failure.Message, "is a link");
        AssertEx.True(File.Exists(bystander), "The refused delete must not have followed the link out of the feature root.");
    }

    /// <summary>
    ///     The check a plan needs and the one <see cref="ExternalAppStorageLayout.Prepare" /> cannot give it: Prepare
    ///     ran before the daemon calls, and the plan carries path strings, which resolve nothing. A component swapped
    ///     for a link in between is refused here, immediately before the container that would bind it is created.
    /// </summary>
    [Test]
    public void VerifyBindSources_WhenAComponentBecameALinkAfterPrepare_IsRefused()
    {
        SymlinkSupport.EnsureSupported();

        var layout = CreateLayout();
        var manifest = ExternalAppTestManifests.Manifest([ExternalAppTestManifests.Service("app", storage: [new ApplicationStorage("data", "/data")])]);
        var paths = layout.Prepare(_instanceId, manifest);
        var plan = PlanFor(layout, manifest);

        // The prepared pass left a real directory here; this is the swap the window allows.
        AssertEx.True(Directory.Exists(paths.VolumePath("app", "data")), "The prepared pass must have created the volume this test replaces.");
        var elsewhere = Path.Combine(_root, "outside-the-instance");
        _ = Directory.CreateDirectory(elsewhere);
        Directory.Delete(paths.VolumePath("app", "data"));
        Directory.CreateSymbolicLink(paths.VolumePath("app", "data"), elsewhere);

        var failure = AssertEx.Throws<ExternalAppStorageException>(() => layout.VerifyBindSources(plan));

        AssertEx.Contains(failure.Message, "is a link");
    }

    [Test]
    public void VerifyBindSources_OnThePathsPrepareJustWrote_Passes()
    {
        var layout = CreateLayout();
        var manifest = ExternalAppTestManifests.Manifest([
            ExternalAppTestManifests.Service("app",
                storage: [new ApplicationStorage("data", "/data")],
                files: [ExternalAppTestManifests.File("settings.yml", "/app/settings.yml", "server: on")])
        ]);
        _ = layout.Prepare(_instanceId, manifest);

        // The negative control for the test above: without this, a VerifyBindSources that refused everything would
        // look just as green. Asserted rather than merely called: a test whose body throws nothing but states nothing
        // has verified nothing, and a reader cannot tell the two apart.
        var plan = PlanFor(layout, manifest);
        AssertEx.DoesNotThrow(() => layout.VerifyBindSources(plan),
            "The paths Prepare just wrote must pass the bind-source check; otherwise the refusal test above proves nothing.");
    }

    [Test]
    public void Describe_PlacesTheInstanceUnderTheFeatureDirectory()
    {
        var paths = CreateLayout().Describe(_instanceId);

        var expected = Path.Combine(_root, "external-apps", "instances", _instanceId.ToString("N"));
        AssertEx.Equal(expected, paths.InstanceRoot);
        AssertEx.Equal(Path.Combine(expected, "volumes"), paths.VolumesRoot);
        AssertEx.Equal(Path.Combine(expected, "files"), paths.FilesRoot);
    }

    /// <summary>
    ///     The asset is written with the mode a secret-bearing file gets. The engine passes no user, so a curated
    ///     image reads it as the identity the bind mount already belongs to.
    /// </summary>
    [Test]
    public void Materialize_OnLinux_WritesTheAssetOwnerOnly()
    {
        var layout = CreateLayout();
        var paths = layout.Prepare(_instanceId, ManifestWithAsset("settings.yml", "server: on"));

        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            AssertEx.True(File.Exists(paths.FilePath("app", "settings.yml")), "Windows has no Unix mode; the file must still be written.");
            return;
        }

        AssertEx.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(paths.FilePath("app", "settings.yml")));
    }

    [Test]
    public void ValidateRelativeName_AcceptsANestedSourceAndRejectsTheRest()
    {
        AssertEx.Equal(Path.Combine("files", "searxng", "settings.yml"),
            ExternalAppStorageLayout.ValidateRelativeName("files/searxng/settings.yml", "files[].source"));

        _ = AssertEx.Throws<ExternalAppStorageException>(() => ExternalAppStorageLayout.ValidateRelativeName("C:/data", "storage[].name"));
        _ = AssertEx.Throws<ExternalAppStorageException>(() => ExternalAppStorageLayout.ValidateRelativeName("a//b", "storage[].name"));
        _ = AssertEx.Throws<ExternalAppStorageException>(() => ExternalAppStorageLayout.ValidateRelativeName(value: null, "storage[].name"));
    }

    private DeploymentPlan PlanFor(ExternalAppStorageLayout layout, ApplicationManifest manifest)
    {
        return DeploymentPlanner.Plan(manifest,
            _instanceId,
            "install-1",
            new Dictionary<string, string>(StringComparer.Ordinal),
            new ResolvedContainerIdentity(1234, 5678),
            [],
            layout.Describe(_instanceId));
    }

    private ExternalAppStorageLayout CreateLayout()
    {
        return new ExternalAppStorageLayout(new FakeNodeDataDirectory(_root), Options.Create(new ExternalAppsOptions()));
    }

    private static ApplicationManifest ManifestWithAsset(string source, string content)
    {
        return ExternalAppTestManifests.Manifest([ExternalAppTestManifests.Service("app", files: [ExternalAppTestManifests.File(source, "/app/settings.yml", content)])]);
    }
}
