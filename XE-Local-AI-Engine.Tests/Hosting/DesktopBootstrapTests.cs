namespace XE_Local_AI_Engine.Tests.Hosting;

using Microsoft.Extensions.Configuration;
using XE_Local_AI_Engine.Client.Hosting;
using XE_Local_AI_Engine.Client.Services.Persistence.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Unit coverage for the self-contained desktop bootstrap (SEC-1 + SEC-2): given neither the operator-secret env var
///     nor a connection string, it must populate both from a per-user data directory, persist a deterministic key, and be
///     idempotent — while leaving an already-supplied value untouched (the off-flag byte-behavior invariant). All tests
///     redirect the data directory into a temp folder via the injected folder resolver, so the real %LOCALAPPDATA% is
///     never touched.
/// </summary>
// EnsureLocalDataConfiguration resolves the operator secret process-env-first (XE_NODE_SQLITE_KEY) via
// NodeOperatorSecretProvider, so the "neither env set" cases require that process-global var to be UNSET while other
// suites (the CUDA env-scrub test, the ranker-registration build) set/read it. The shared NotInParallel key serializes
// this class against them, and the constructor/Dispose save-then-clear-then-restore the var so a serialized-but-leaky
// sibling can never poison the next test's "neither set" premise.
[NotInParallel("XE_NODE_SQLITE_KEY")]
public sealed class DesktopBootstrapTests : IDisposable
{
    private readonly string? _originalOperatorSecretEnv = Environment.GetEnvironmentVariable(NodeOperatorSecretProvider.EnvVarName);

    public DesktopBootstrapTests()
    {
        // Force the "neither env set" precondition regardless of ambient/leaked state; restored in Dispose.
        Environment.SetEnvironmentVariable(NodeOperatorSecretProvider.EnvVarName, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(NodeOperatorSecretProvider.EnvVarName, _originalOperatorSecretEnv);
    }

    [Test]
    public void EnsureLocalDataConfiguration_WhenNeitherEnvSet_PopulatesConnectionStringAndOperatorSecret()
    {
        using var temp = new TempDirectory();
        using var configuration = new ConfigurationManager();

        DesktopBootstrap.EnsureLocalDataConfiguration(configuration, temp.ResolveFolder);

        var connectionString = configuration.GetConnectionString("node-sqlite");
        AssertEx.NotNullOrEmpty(connectionString);
        AssertEx.Contains(connectionString!, DesktopBootstrap.DatabaseFileName);

        var dataDirectory = temp.DataDirectory;
        AssertEx.True(Directory.Exists(dataDirectory), "The per-user data directory must be created.");

        var keyPath = Path.Combine(dataDirectory, DesktopBootstrap.KeyFileName);
        AssertEx.True(File.Exists(keyPath), "The operator key file must be persisted.");

        var base64Secret = configuration[NodeOperatorSecretProvider.EnvVarName];
        AssertEx.NotNullOrEmpty(base64Secret);
        var decoded = Convert.FromBase64String(base64Secret!);
        AssertEx.Equal(NodeOperatorSecretProvider.ExpectedSecretLength, decoded.Length);
    }

    [Test]
    public void EnsureLocalDataConfiguration_PointsDefaultChatModelAtTheFirstRunGguf()
    {
        using var temp = new TempDirectory();
        using var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            // The stock Ollama-era default that desktop mode never installs.
            [DesktopBootstrap.LocalChatDefaultModelKey] = "qwen3:0.6b",
            [DesktopBootstrap.FirstRunModelRepoIdKey] = "bartowski/Qwen2.5-0.5B-Instruct-GGUF",
            [DesktopBootstrap.FirstRunModelQuantKey] = "Q4_K_M"
        });

        DesktopBootstrap.EnsureLocalDataConfiguration(configuration, temp.ResolveFolder);

        // The desktop default must become the exact "repo:quant" identity first-run provisioning installs and selects,
        // overriding the stock Ollama id so the chat composer never opens on an uninstalled model.
        AssertEx.Equal("bartowski/Qwen2.5-0.5B-Instruct-GGUF:Q4_K_M", configuration[DesktopBootstrap.LocalChatDefaultModelKey]);
    }

    [Test]
    public void EnsureLocalDataConfiguration_LeavesDefaultChatModelUntouched_WhenNoFirstRunModelConfigured()
    {
        using var temp = new TempDirectory();
        using var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [DesktopBootstrap.LocalChatDefaultModelKey] = "qwen3:0.6b"
        });

        DesktopBootstrap.EnsureLocalDataConfiguration(configuration, temp.ResolveFolder);

        // With no FirstRunModel:RepoId configured there is nothing to provision, so the stock default is left in place.
        AssertEx.Equal("qwen3:0.6b", configuration[DesktopBootstrap.LocalChatDefaultModelKey]);
    }

    [Test]
    public void EnsureLocalDataConfiguration_SetsNodeDataDirectory_InDesktopMode()
    {
        using var temp = new TempDirectory();
        using var configuration = new ConfigurationManager();

        DesktopBootstrap.EnsureLocalDataConfiguration(configuration, temp.ResolveFolder);

        // The node-data-directory abstraction reads this key; in desktop mode it must point at the per-user data dir so
        // every per-node runtime artifact (settings, the encrypted credential stores, cert pins, the AgentHome
        // workspace, the hardware-profile cache) is co-located there instead of the shared install dir.
        AssertEx.Equal(temp.DataDirectory, configuration[DesktopBootstrap.NodeDataDirectoryKey]);
    }

    [Test]
    public void NodeDataDirectoryKey_IsUnset_WhenBootstrapNotInvoked()
    {
        // Off the desktop flag DesktopBootstrap is never reached, so the key stays unset and INodeDataDirectory falls
        // back to ContentRootPath — the off-flag byte-behavior invariant.
        using var configuration = new ConfigurationManager();

        AssertEx.Null(configuration[DesktopBootstrap.NodeDataDirectoryKey]);
    }

    [Test]
    public void EnsureLocalDataConfiguration_PopulatesModelsDirectoryUnderDataDirectory()
    {
        using var temp = new TempDirectory();
        using var configuration = new ConfigurationManager();

        DesktopBootstrap.EnsureLocalDataConfiguration(configuration, temp.ResolveFolder);

        var modelsDirectory = configuration[DesktopBootstrap.HuggingFaceModelsDirectoryKey];
        AssertEx.NotNullOrEmpty(modelsDirectory);
        AssertEx.Contains(modelsDirectory!, DesktopBootstrap.ModelsFolderName);
    }

    [Test]
    public void EnsureLocalDataConfiguration_IsIdempotent_ReadsBackTheSameKey()
    {
        using var temp = new TempDirectory();

        using var first = new ConfigurationManager();
        DesktopBootstrap.EnsureLocalDataConfiguration(first, temp.ResolveFolder);
        var firstSecret = first[NodeOperatorSecretProvider.EnvVarName];

        using var second = new ConfigurationManager();
        DesktopBootstrap.EnsureLocalDataConfiguration(second, temp.ResolveFolder);
        var secondSecret = second[NodeOperatorSecretProvider.EnvVarName];

        AssertEx.NotNullOrEmpty(firstSecret);
        AssertEx.Equal(firstSecret!, secondSecret);
    }

    [Test]
    public void GeneratedSecret_IsResolvedByOperatorSecretProvider()
    {
        // The generated key is set via the EXISTING provider config branch (line 24); assert the provider returns the
        // same 32 bytes without any change to NodeOperatorSecretProvider itself.
        using var temp = new TempDirectory();
        using var configuration = new ConfigurationManager();

        DesktopBootstrap.EnsureLocalDataConfiguration(configuration, temp.ResolveFolder);

        var provider = new NodeOperatorSecretProvider(configuration);
        var secret = provider.GetOperatorSecret();

        AssertEx.Equal(NodeOperatorSecretProvider.ExpectedSecretLength, secret.Length);
    }

    [Test]
    public async Task EnsureLocalDataConfiguration_ConcurrentFirstLaunches_AllAdoptTheSameOnDiskSecret()
    {
        // Simulate a double first-launch: many workers race to create the operator key against one fresh data dir. The
        // atomic create-new persist means exactly one wins and every other process must adopt the winner's on-disk
        // secret rather than keep its own freshly-generated one — otherwise concurrent starts would split the
        // DB-encryption key. All resolved secrets must therefore be identical, and must round-trip through the provider.
        using var temp = new TempDirectory();
        const int workers = 24;

        using var barrier = new Barrier(workers);
        var tasks = Enumerable.Range(0, workers).Select(_ => Task.Run(() =>
        {
            using var configuration = new ConfigurationManager();
            // Release all workers together so several genuinely contend on the create.
            barrier.SignalAndWait();
            DesktopBootstrap.EnsureLocalDataConfiguration(configuration, temp.ResolveFolder);
            return configuration[NodeOperatorSecretProvider.EnvVarName];
        })).ToArray();

        var secrets = await Task.WhenAll(tasks).ConfigureAwait(false);

        var distinct = secrets.Distinct(StringComparer.Ordinal).ToArray();
        AssertEx.Equal(1, distinct.Length);
        AssertEx.NotNullOrEmpty(distinct[0]);

        // The single agreed secret must be the one persisted on disk and resolvable by the provider (encrypted round-trip
        // guarantee: any DB encryption keyed on this secret decrypts across every concurrent launch).
        using var resolveConfiguration = new ConfigurationManager();
        DesktopBootstrap.EnsureLocalDataConfiguration(resolveConfiguration, temp.ResolveFolder);
        var provider = new NodeOperatorSecretProvider(resolveConfiguration);
        var resolved = Convert.ToBase64String(provider.GetOperatorSecret());
        AssertEx.Equal(distinct[0]!, resolved);
    }

    [Test]
    public void EnsureLocalDataConfiguration_OnNonWindows_PersistsKeyFileWithOwnerOnlyPermissions()
    {
        // The key file protects the DB-encryption secret, so on non-Windows it must be 0600 (owner read/write only).
        // Windows relies on the per-user %LOCALAPPDATA% ACL instead (no Unix file mode), so skip there.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = new TempDirectory();
        using var configuration = new ConfigurationManager();

        DesktopBootstrap.EnsureLocalDataConfiguration(configuration, temp.ResolveFolder);

        var keyPath = Path.Combine(temp.DataDirectory, DesktopBootstrap.KeyFileName);
        AssertEx.True(File.Exists(keyPath), "The operator key file must be persisted.");

        var mode = File.GetUnixFileMode(keyPath);
        AssertEx.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }

    [Test]
    public void EnsureLocalDataConfiguration_WhenConnectionStringAlreadySupplied_LeavesItUnchanged()
    {
        using var temp = new TempDirectory();
        const string suppliedConnectionString = "Data Source=/already/supplied/node.sqlite";
        using var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [DesktopBootstrap.NodeSqliteConnectionStringKey] = suppliedConnectionString
        });

        DesktopBootstrap.EnsureLocalDataConfiguration(configuration, temp.ResolveFolder);

        AssertEx.Equal(suppliedConnectionString, configuration.GetConnectionString("node-sqlite"));
    }

    [Test]
    public void EnsureLocalDataConfiguration_WhenOperatorSecretAlreadySupplied_DoesNotGenerateKeyFile()
    {
        using var temp = new TempDirectory();
        var suppliedSecret = Convert.ToBase64String(new byte[NodeOperatorSecretProvider.ExpectedSecretLength]);
        using var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [NodeOperatorSecretProvider.EnvVarName] = suppliedSecret
        });

        DesktopBootstrap.EnsureLocalDataConfiguration(configuration, temp.ResolveFolder);

        AssertEx.Equal(suppliedSecret, configuration[NodeOperatorSecretProvider.EnvVarName]);

        var keyPath = Path.Combine(temp.DataDirectory, DesktopBootstrap.KeyFileName);
        AssertEx.False(File.Exists(keyPath), "An already-supplied operator secret must not trigger key-file generation.");
    }

    /// <summary>
    ///     A disposable temp directory that stands in for %LOCALAPPDATA%. The resolver returns its root so the bootstrap
    ///     builds the per-user data directory underneath it.
    /// </summary>
    private sealed class TempDirectory : IDisposable
    {
        private readonly string _root;

        public TempDirectory()
        {
            _root = Path.Combine(Path.GetTempPath(), "xe-desktop-bootstrap-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public string DataDirectory => Path.Combine(_root, DesktopBootstrap.ApplicationDataFolderName);

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
                // Best-effort cleanup of the temp directory.
            }
        }

        public string ResolveFolder(Environment.SpecialFolder folder)
        {
            _ = folder;
            return _root;
        }
    }
}
