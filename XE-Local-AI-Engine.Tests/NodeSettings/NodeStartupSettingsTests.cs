namespace XE_Local_AI_Engine.Tests.NodeSettings;

using System.Runtime.Versioning;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.NodeSettings.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

/// <summary>
///     The pre-host read of the structural switches: stored beats the appsettings seed, the seed beats the code default,
///     and the file is found where <see cref="NodeDataDirectory" /> would leave it after its first-launch migration.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class NodeStartupSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "xe-node-startup-settings-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public void Read_WhenNoFileAndNoSeeds_ReturnsTheCodeDefaults()
    {
        var sut = Read(contentRoot: Dir("content"));

        AssertEx.True(sut.DevelopmentEnabled);
        AssertEx.True(sut.SchedulerEnabled);
        AssertEx.False(sut.ExternalAppsEnabled);
    }

    [Test]
    public void Read_WhenNoFile_ReturnsTheSeeds()
    {
        var sut = Read(contentRoot: Dir("content"), seeds: new()
        {
            ["Development:Enabled"] = "false",
            ["Scheduler:Enabled"] = "false",
            ["ExternalApps:Enabled"] = "true"
        });

        AssertEx.False(sut.DevelopmentEnabled);
        AssertEx.False(sut.SchedulerEnabled);
        AssertEx.True(sut.ExternalAppsEnabled);
    }

    [Test]
    public void Read_WhenStored_StoredBeatsTheSeed()
    {
        var content = Dir("content");
        Write(content, """{ "developmentEnabled": true, "schedulerEnabled": false, "externalAppsEnabled": false }""");

        var sut = Read(contentRoot: content, seeds: new()
        {
            ["Development:Enabled"] = "false",
            ["Scheduler:Enabled"] = "true",
            ["ExternalApps:Enabled"] = "true"
        });

        AssertEx.True(sut.DevelopmentEnabled);
        AssertEx.False(sut.SchedulerEnabled);
        AssertEx.False(sut.ExternalAppsEnabled);
    }

    [Test]
    public void Read_WhenOnlySomeFieldsAreStored_TheRestFallToTheSeed()
    {
        var content = Dir("content");
        Write(content, """{ "schedulerEnabled": false, "enableTools": true }""");

        var sut = Read(contentRoot: content, seeds: new()
        {
            ["ExternalApps:Enabled"] = "true"
        });

        AssertEx.True(sut.DevelopmentEnabled);
        AssertEx.False(sut.SchedulerEnabled);
        AssertEx.True(sut.ExternalAppsEnabled);
    }

    [Test]
    public void Read_WhenTheFileIsInvalid_ReturnsTheSeedsAndLogsAWarning()
    {
        var content = Dir("content");
        Write(content, "{ not json");
        var logger = new RecordingLogger<NodeSettingsStore>();

        var sut = Read(contentRoot: content, seeds: new()
        {
            ["Development:Enabled"] = "false"
        }, logger: logger);

        AssertEx.False(sut.DevelopmentEnabled);
        AssertEx.True(sut.SchedulerEnabled);
        AssertEx.True(logger.HasEntry(LogLevel.Warning, "could not be deserialized"),
            string.Join(" | ", logger.Entries.Select(static entry => entry.Message)));
    }

    [Test]
    [ExcludeOn(OS.Windows)]
    [UnsupportedOSPlatform("windows")]
    public void Read_WhenTheFileIsUnreadableByPermission_ReturnsTheSeedsAndLogsAWarning()
    {
        if (Environment.IsPrivilegedProcess)
        {
            Skip.Test("BLOCKED: a privileged process bypasses the file mode this test denies the read with.");
        }

        var content = Dir("content");
        Write(content, """{ "developmentEnabled": true }""");
        var path = Path.Combine(content, "node-settings.json");
        File.SetUnixFileMode(path, UnixFileMode.None);
        var logger = new RecordingLogger<NodeSettingsStore>();
        try
        {
            var sut = Read(contentRoot: content, seeds: new()
            {
                ["Development:Enabled"] = "false"
            }, logger: logger);

            AssertEx.False(sut.DevelopmentEnabled, "An unreadable file must fall back to the seed, not crash startup.");
            AssertEx.True(logger.HasEntry(LogLevel.Warning, "could not be read"),
                string.Join(" | ", logger.Entries.Select(static entry => entry.Message)));
        }
        finally
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Test]
    public void Read_WhenTheDataDirectoryIsConfigured_ReadsThatFile()
    {
        var content = Dir("content");
        var data = Dir("data");
        Write(data, """{ "developmentEnabled": false }""");

        var sut = Read(contentRoot: content, dataDirectory: data);

        AssertEx.False(sut.DevelopmentEnabled);
    }

    [Test]
    public void Read_WhenOnlyTheLegacyContentRootFileExists_HonoursIt()
    {
        // NodeDataDirectory moves this file into the data dir on first launch, but only after the pre-host read.
        var content = Dir("content");
        var data = Dir("data");
        Write(content, """{ "schedulerEnabled": false }""");

        var sut = Read(contentRoot: content, dataDirectory: data);

        AssertEx.False(sut.SchedulerEnabled);
    }

    [Test]
    public void Read_WhenBothFilesExist_TheDataDirectoryFileWins()
    {
        var content = Dir("content");
        var data = Dir("data");
        Write(content, """{ "schedulerEnabled": false }""");
        Write(data, """{ "schedulerEnabled": true }""");

        var sut = Read(contentRoot: content, dataDirectory: data);

        AssertEx.True(sut.SchedulerEnabled);
    }

    [Test]
    public void Read_WhenTheLegacyFileIsExcluded_IgnoresIt()
    {
        var content = Dir("content");
        var data = Dir("data");
        Write(content, """{ "schedulerEnabled": false }""");

        var sut = Read(contentRoot: content, dataDirectory: data, includeLegacyContentRoot: false);

        AssertEx.True(sut.SchedulerEnabled);
    }

    [Test]
    public void Read_NeverMovesTheLegacyFile()
    {
        // The migration belongs to NodeDataDirectory; a read that moved the file would race the host it runs in.
        var content = Dir("content");
        var data = Dir("data");
        Write(content, """{ "schedulerEnabled": false }""");

        _ = Read(contentRoot: content, dataDirectory: data);

        AssertEx.True(File.Exists(Path.Combine(content, "node-settings.json")));
        AssertEx.False(File.Exists(Path.Combine(data, "node-settings.json")));
    }

    private static NodeStartupSettings Read(string contentRoot,
        string? dataDirectory = null,
        Dictionary<string, string?>? seeds = null,
        RecordingLogger<NodeSettingsStore>? logger = null,
        bool includeLegacyContentRoot = true)
    {
        var values = new Dictionary<string, string?>(seeds ?? [], StringComparer.Ordinal)
        {
            [NodeDataDirectory.ConfigurationKey] = dataDirectory
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var environment = Substitute.For<IHostEnvironment>();
        environment.ContentRootPath.Returns(contentRoot);

        return NodeStartupSettings.Read(configuration, environment, logger, includeLegacyContentRoot);
    }

    private static void Write(string directory, string json) =>
        File.WriteAllText(Path.Combine(directory, "node-settings.json"), json);

    private string Dir(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }
}
