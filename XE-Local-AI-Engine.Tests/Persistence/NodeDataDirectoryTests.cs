namespace XE_Local_AI_Engine.Tests.Persistence;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.NodeSettings.Implementation;
using XE_Local_AI_Engine.Client.Services.Persistence.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The node-data-directory abstraction is the single seam that relocates per-node runtime state into the per-user
///     data dir in desktop mode while staying byte-identical (rooted at the content root) off the desktop flag, and its
///     first-launch migration carries a broken-RC tester's existing artifacts across. The settings store rounds-trips
///     through that root and applies owner-only perms on non-Windows to match the key-file posture.
/// </summary>
public sealed class NodeDataDirectoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "xe-node-data-dir-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public void Root_WhenConfigured_ResolvesToConfiguredRoot()
    {
        var configuredRoot = Path.Combine(_root, "data");
        Directory.CreateDirectory(configuredRoot);
        var sut = CreateSut(configuredRoot, Path.Combine(_root, "content"));

        AssertEx.Equal(configuredRoot, sut.Root);
    }

    [Test]
    public void Root_WhenUnset_FallsBackToContentRoot()
    {
        var contentRoot = Path.Combine(_root, "content");
        Directory.CreateDirectory(contentRoot);

        var sut = CreateSut(configuredRoot: null, contentRoot);

        AssertEx.Equal(contentRoot, sut.Root);
    }

    [Test]
    public void Constructor_WhenExistingContentRootArtifacts_MovesThemIntoTheDataDirectory()
    {
        var contentRoot = Path.Combine(_root, "content");
        var dataDir = Path.Combine(_root, "data");
        Directory.CreateDirectory(contentRoot);

        // A tester upgrading from the broken RC has these in the shared install dir.
        File.WriteAllText(Path.Combine(contentRoot, "node-settings.json"), "{\"defaultModelName\":\"keep-me\"}");
        File.WriteAllBytes(Path.Combine(contentRoot, "worker-credentials.enc"), [1, 2, 3]);

        var sut = CreateSut(dataDir, contentRoot);

        AssertEx.Equal(dataDir, sut.Root);
        AssertEx.True(File.Exists(Path.Combine(dataDir, "node-settings.json")), "node-settings.json must migrate into the data dir.");
        AssertEx.True(File.Exists(Path.Combine(dataDir, "worker-credentials.enc")), "the encrypted credential must migrate into the data dir.");
        AssertEx.False(File.Exists(Path.Combine(contentRoot, "node-settings.json")), "the migrated artifact must be moved, not copied.");
    }

    [Test]
    public void Constructor_WhenDataDirAlreadyHasArtifact_DoesNotClobberIt()
    {
        var contentRoot = Path.Combine(_root, "content");
        var dataDir = Path.Combine(_root, "data");
        Directory.CreateDirectory(contentRoot);
        Directory.CreateDirectory(dataDir);

        File.WriteAllText(Path.Combine(contentRoot, "node-settings.json"), "{\"defaultModelName\":\"stale-from-install\"}");
        File.WriteAllText(Path.Combine(dataDir, "node-settings.json"), "{\"defaultModelName\":\"canonical\"}");

        _ = CreateSut(dataDir, contentRoot);

        // The data dir is canonical: a pre-existing file there must win over the install-dir copy.
        AssertEx.Contains(File.ReadAllText(Path.Combine(dataDir, "node-settings.json")), "canonical");
    }

    [Test]
    public void Constructor_WhenRootEqualsContentRoot_DoesNotMigrate()
    {
        // Off the desktop flag Root == ContentRootPath; the migration must be a no-op (it would otherwise move a
        // headless node's live files onto themselves).
        var contentRoot = Path.Combine(_root, "content");
        Directory.CreateDirectory(contentRoot);
        File.WriteAllText(Path.Combine(contentRoot, "node-settings.json"), "{}");

        var sut = CreateSut(configuredRoot: null, contentRoot);

        AssertEx.Equal(contentRoot, sut.Root);
        AssertEx.True(File.Exists(Path.Combine(contentRoot, "node-settings.json")), "the headless file must stay in place.");
    }

    [Test]
    public async Task NodeSettingsStore_ReadsAndWritesUnderDataDirectory_RoundTrip()
    {
        var dataDir = Path.Combine(_root, "data");
        Directory.CreateDirectory(dataDir);
        using var store = new NodeSettingsStore(new FakeNodeDataDirectory(dataDir), NullLogger<NodeSettingsStore>.Instance);

        await store.SaveAsync(new StoredNodeSettings
        {
            DefaultModelName = "my-model",
            MaxMessageRequestTimeoutSeconds = 120
        });

        AssertEx.True(File.Exists(Path.Combine(dataDir, "node-settings.json")), "the settings file must live under the data dir.");

        var loaded = await store.LoadAsync();
        AssertEx.Equal("my-model", loaded.DefaultModelName);
        AssertEx.Equal(expected: 120, loaded.MaxMessageRequestTimeoutSeconds);
    }

    [Test]
    public async Task NodeSettingsStore_OnNonWindows_CreatesSettingsFileWith0600()
    {
        // The settings file joins the key file in the data dir; on non-Windows it must be owner read/write only.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var dataDir = Path.Combine(_root, "data");
        Directory.CreateDirectory(dataDir);
        using var store = new NodeSettingsStore(new FakeNodeDataDirectory(dataDir), NullLogger<NodeSettingsStore>.Instance);

        await store.SaveAsync(new StoredNodeSettings
        {
            DefaultModelName = "my-model"
        });

        var mode = File.GetUnixFileMode(Path.Combine(dataDir, "node-settings.json"));
        AssertEx.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }

    private static NodeDataDirectory CreateSut(string? configuredRoot, string contentRoot)
    {
        var configuration = new ConfigurationBuilder()
                            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
                            {
                                [NodeDataDirectory.ConfigurationKey] = configuredRoot
                            })
                            .Build();

        var hostEnvironment = Substitute.For<IHostEnvironment>();
        hostEnvironment.ContentRootPath.Returns(contentRoot);

        return new NodeDataDirectory(configuration, hostEnvironment, NullLogger<NodeDataDirectory>.Instance);
    }
}
