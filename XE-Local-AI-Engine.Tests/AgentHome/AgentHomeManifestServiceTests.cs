namespace XE_Local_AI_Engine.Tests.AgentHome;

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Configuration.Validation;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Fake;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class AgentHomeManifestServiceTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 5, 29, 12, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions ManifestSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly List<string> _tempRoots = [];

    public void Dispose()
    {
        foreach (var root in _tempRoots)
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup of the temp host root.
            }
        }
    }

    [Test]
    public async Task InitializeAsync_OnEmptyRoot_WritesCompleteLayout()
    {
        var clock = new MutableTimeProvider(FixedNow);
        using var service = CreateService(NewTempRoot(), clock, new FakeSandboxRuntimeProvider(clock));

        var layout = await service.InitializeAsync(Key());

        AssertEx.Equal(AgentHomeStatus.Ready, layout.Manifest.Status);
        AssertEx.Equal(AgentHomeManifest.CurrentVersion, layout.Manifest.Version);
        AssertEx.Equal("owner-a", layout.Manifest.OwnerUserId);
        AssertEx.Equal(FixedNow, layout.Manifest.CreatedAt);
        AssertEx.True(
            AgentHomeLayoutMap.Directories.All(directory => Directory.Exists(Path.Combine(layout.RootPath, directory))),
            "every layout directory should exist");
        AssertEx.True(File.Exists(Path.Combine(layout.RootPath, "manifest.json")));
        AssertEx.True(File.Exists(Path.Combine(layout.RootPath, "policy.json")));
        AssertEx.True(File.Exists(Path.Combine(layout.RootPath, "README.agent-home.md")));
        AssertEx.True(File.Exists(Path.Combine(layout.RootPath, "skills", "registry.json")));
        AssertEx.True(File.Exists(Path.Combine(layout.RootPath, "tools", "registry.json")));
        AssertEx.True(File.Exists(Path.Combine(layout.RootPath, "logs", "events.jsonl")));
        AssertEx.True(File.Exists(Path.Combine(layout.RootPath, "memory", "proposals", "node-memory.proposals.jsonl")));
        AssertEx.True(File.Exists(Path.Combine(layout.RootPath, "agents", "primary", "main", "plan.md")));
    }

    [Test]
    public async Task InitializeAsync_WritesParseableManifest()
    {
        var clock = new MutableTimeProvider(FixedNow);
        using var service = CreateService(NewTempRoot(), clock, new FakeSandboxRuntimeProvider(clock));

        var layout = await service.InitializeAsync(Key());

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(layout.RootPath, "manifest.json")));
        AssertEx.Equal("Ready", document.RootElement.GetProperty("status").GetString());
        AssertEx.Equal(1, document.RootElement.GetProperty("version").GetInt32());
        AssertEx.Equal("owner-a", document.RootElement.GetProperty("ownerUserId").GetString());
    }

    [Test]
    public async Task InitializeAsync_WritesMinimalValidRegistries()
    {
        var clock = new MutableTimeProvider(FixedNow);
        using var service = CreateService(NewTempRoot(), clock, new FakeSandboxRuntimeProvider(clock));

        var layout = await service.InitializeAsync(Key());

        using var skills = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(layout.RootPath, "skills", "registry.json")));
        AssertEx.Equal(1, skills.RootElement.GetProperty("version").GetInt32());
        AssertEx.Equal(0, skills.RootElement.GetProperty("skills").GetArrayLength());

        using var tools = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(layout.RootPath, "tools", "registry.json")));
        AssertEx.Equal(1, tools.RootElement.GetProperty("version").GetInt32());
        AssertEx.Equal(0, tools.RootElement.GetProperty("tools").GetArrayLength());
    }

    [Test]
    public async Task InitializeAsync_WhenRerunSameOwner_IsNoOp()
    {
        var clock = new MutableTimeProvider(FixedNow);
        using var service = CreateService(NewTempRoot(), clock, new FakeSandboxRuntimeProvider(clock));

        var first = await service.InitializeAsync(Key());
        var policyPath = Path.Combine(first.RootPath, "policy.json");
        await File.WriteAllTextAsync(policyPath, "SENTINEL");
        clock.Advance(TimeSpan.FromMinutes(5));

        var second = await service.InitializeAsync(Key());

        AssertEx.Equal(AgentHomeStatus.Ready, second.Manifest.Status);
        AssertEx.Equal(first.Manifest.UpdatedAt, second.Manifest.UpdatedAt);
        AssertEx.Equal("SENTINEL", await File.ReadAllTextAsync(policyPath));
    }

    [Test]
    public async Task InitializeAsync_WhenLayoutPartial_SelfHeals()
    {
        var clock = new MutableTimeProvider(FixedNow);
        using var service = CreateService(NewTempRoot(), clock, new FakeSandboxRuntimeProvider(clock));

        var first = await service.InitializeAsync(Key());
        var preservedPath = Path.Combine(first.RootPath, "policy.json");
        await File.WriteAllTextAsync(preservedPath, "SENTINEL");
        File.Delete(Path.Combine(first.RootPath, "tools", "registry.json"));
        File.Delete(Path.Combine(first.RootPath, "README.agent-home.md"));
        Directory.Delete(Path.Combine(first.RootPath, "skills"), recursive: true);

        var healed = await service.InitializeAsync(Key());

        AssertEx.Equal(AgentHomeStatus.Ready, healed.Manifest.Status);
        AssertEx.True(File.Exists(Path.Combine(healed.RootPath, "tools", "registry.json")), "deleted file recreated");
        AssertEx.True(File.Exists(Path.Combine(healed.RootPath, "README.agent-home.md")), "deleted file recreated");
        AssertEx.True(File.Exists(Path.Combine(healed.RootPath, "skills", "registry.json")), "deleted directory recreated");
        AssertEx.Equal("SENTINEL", await File.ReadAllTextAsync(preservedPath));
    }

    [Test]
    public async Task InitializeAsync_WhenManifestStuckInitializing_ReinitializesWhenStale()
    {
        var clock = new MutableTimeProvider(FixedNow);
        using var service = CreateService(NewTempRoot(), clock, new FakeSandboxRuntimeProvider(clock));

        var first = await service.InitializeAsync(Key());
        var sentinel = Path.Combine(first.RootPath, "workspace", "selected", "marker.txt");
        await File.WriteAllTextAsync(sentinel, "keep");
        WriteManifestFile(first.RootPath, StuckInitializingManifest());
        clock.Advance(TimeSpan.FromSeconds(1801));

        var reinitialized = await service.InitializeAsync(Key());

        AssertEx.Equal(AgentHomeStatus.Ready, reinitialized.Manifest.Status);
        AssertEx.False(File.Exists(sentinel), "a stale initializing layout is wiped and reinitialized");
    }

    [Test]
    public async Task InitializeAsync_WhenManifestStructurallyIncomplete_ReinitializesAsReady()
    {
        var clock = new MutableTimeProvider(FixedNow);
        using var service = CreateService(NewTempRoot(), clock, new FakeSandboxRuntimeProvider(clock));

        var first = await service.InitializeAsync(Key());
        await File.WriteAllTextAsync(Path.Combine(first.RootPath, "manifest.json"), "{}");

        var recovered = await service.InitializeAsync(Key());

        AssertEx.Equal(AgentHomeStatus.Ready, recovered.Manifest.Status);
        AssertEx.Equal("owner-a", recovered.Manifest.OwnerUserId);
    }

    [Test]
    public async Task InitializeAsync_WhenOwnerChanges_WipesWorkspaceContents()
    {
        var clock = new MutableTimeProvider(FixedNow);
        using var service = CreateService(NewTempRoot(), clock, new FakeSandboxRuntimeProvider(clock));

        var layoutA = await service.InitializeAsync(Key("owner-a"));
        var sentinel = Path.Combine(layoutA.RootPath, "workspace", "selected", "data.txt");
        await File.WriteAllTextAsync(sentinel, "secret");

        var layoutB = await service.InitializeAsync(Key("owner-b"));

        AssertEx.Equal("owner-b", layoutB.Manifest.OwnerUserId);
        AssertEx.False(File.Exists(sentinel), "an owner change must not reuse copied workspace contents");
    }

    [Test]
    public async Task InitializeAsync_WhenOwnerChanges_KillsPriorSandbox()
    {
        var clock = new MutableTimeProvider(FixedNow);
        var provider = new FakeSandboxRuntimeProvider(clock);
        using var service = CreateService(NewTempRoot(), clock, provider);

        await service.InitializeAsync(Key("owner-a"));
        await provider.CreateOrAttachAsync(new SandboxCreateRequest
        {
            AttachKey = Key("owner-a"),
            RuntimeProfile = "dotnet-agent-home"
        });

        await service.InitializeAsync(Key("owner-b"));

        await AssertEx.ThrowsAsync<SandboxHandleInvalidException>(() => provider.ConnectAsync(Key("owner-a")));
    }

    [Test]
    public async Task InitializeAsync_WhenRootPathNull_UsesContentRoot()
    {
        var clock = new MutableTimeProvider(FixedNow);
        var contentRoot = NewTempRoot();
        Directory.CreateDirectory(contentRoot);
        using var service = new AgentHomeManifestService(
            new TestHostEnvironment { ContentRootPath = contentRoot },
            Options.Create(new AgentHomeOptions { RootPath = null }),
            new FakeSandboxRuntimeProvider(clock),
            clock,
            NullLogger<AgentHomeManifestService>.Instance);

        var layout = await service.InitializeAsync(Key());

        var expected = Path.Combine(contentRoot, "agent-home-state", "agent-home");
        AssertEx.Equal(expected, layout.RootPath);
        AssertEx.True(Directory.Exists(expected));
    }

    [Test]
    public void Validate_WhenValid_Succeeds()
    {
        var result = new AgentHomeOptionsValidator().Validate(null, new AgentHomeOptions());

        AssertEx.True(result.Succeeded);
    }

    [Test]
    public void Validate_WhenStaleSecondsNotPositive_Fails()
    {
        var result = new AgentHomeOptionsValidator().Validate(null, new AgentHomeOptions { PrepareStaleAfterSeconds = 0 });

        AssertEx.True(result.Failed);
    }

    [Test]
    public void Validate_WhenRootPathBlank_Fails()
    {
        var result = new AgentHomeOptionsValidator().Validate(null, new AgentHomeOptions { RootPath = "  " });

        AssertEx.True(result.Failed);
    }

    private static AgentHomeManifestService CreateService(
        string rootPath,
        TimeProvider clock,
        ISandboxRuntimeProvider provider,
        int staleSeconds = 1800)
    {
        var options = Options.Create(new AgentHomeOptions { RootPath = rootPath, PrepareStaleAfterSeconds = staleSeconds });
        var hostEnvironment = new TestHostEnvironment { ContentRootPath = rootPath };
        return new AgentHomeManifestService(hostEnvironment, options, provider, clock, NullLogger<AgentHomeManifestService>.Instance);
    }

    private static SandboxAttachKey Key(string owner = "owner-a")
    {
        return new SandboxAttachKey
        {
            OwnerUserId = owner,
            NodeId = "node-1",
            ProviderName = "fake",
            RuntimeProfile = "dotnet-agent-home",
            ManifestVersion = AgentHomeManifest.CurrentVersion
        };
    }

    private static AgentHomeManifest StuckInitializingManifest()
    {
        return new AgentHomeManifest
        {
            Version = AgentHomeManifest.CurrentVersion,
            Status = AgentHomeStatus.Initializing,
            OwnerUserId = "owner-a",
            NodeId = "node-1",
            ProviderName = "fake",
            RuntimeProfile = "dotnet-agent-home",
            CreatedAt = FixedNow,
            UpdatedAt = FixedNow
        };
    }

    private static void WriteManifestFile(string agentHomeRoot, AgentHomeManifest manifest)
    {
        File.WriteAllText(Path.Combine(agentHomeRoot, "manifest.json"), JsonSerializer.Serialize(manifest, ManifestSerializerOptions));
    }

    private string NewTempRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "agenthome-test-" + Guid.NewGuid().ToString("N"));
        _tempRoots.Add(path);
        return path;
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public MutableTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public void Advance(TimeSpan delta)
        {
            _utcNow = _utcNow.Add(delta);
        }

        public override DateTimeOffset GetUtcNow()
        {
            return _utcNow;
        }
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "tests";

        public string EnvironmentName { get; set; } = "Development";

        public string ContentRootPath { get; set; } = string.Empty;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
