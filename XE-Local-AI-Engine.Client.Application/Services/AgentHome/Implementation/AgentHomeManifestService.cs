namespace XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     Writes and recovers the worker-local <c>agent-home</c> layout on the deterministic host root. The sandbox provider
///     abstraction cannot author a directory tree (copy-into needs a host source, there is no mkdir, exec is scripted),
///     and the layout must exist while Agent Mode is disabled, so the layout is materialized on the host root via
///     <see cref="System.IO" />; later workspace-copy steps copy the prepared tree into the sandbox. The provider is
///     consumed only to kill prior runtime state on an owner change.
/// </summary>
internal sealed class AgentHomeManifestService : IAgentHomeManifestService, IDisposable
{
    private const string AgentHomeDirectoryName = "agent-home";
    private const string DefaultRootDirectoryName = "agent-home-state";
    private const string ManifestFileName = "manifest.json";
    private const string PolicyFileName = "policy.json";
    private const string ReadmeFileName = "README.agent-home.md";
    private const string LockFileName = ".agent-home.lock";

    private const string ReadmeContent =
        "# AgentHome\n\nNode-scoped sandbox runtime workspace for Agent Mode. This tree is initialized and recovered by the worker; do not edit generated files by hand. See manifest.json for the current version and status.\n";

    private const string SkillsReadmeContent =
        "# Skills\n\nSkills available to the AgentHome runtime. registry.json is generated; do not edit by hand.\n";

    private const string PlanContent = "# Plan\n\nPrimary agent planning document. Generated baseline; replaced at runtime.\n";
    private const string ScratchpadContent = "# Scratchpad\n\nPrimary agent scratch space. Generated baseline; replaced at runtime.\n";
    private const string FindingsContent = "# Findings\n\nPrimary agent findings. Generated baseline; replaced at runtime.\n";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters =
        {
            new JsonStringEnumConverter()
        }
    };

    private readonly string _contentRootPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger<AgentHomeManifestService> _logger;
    private readonly AgentHomeOptions _options;
    private readonly ISandboxRuntimeProvider _sandboxProvider;
    private readonly TimeProvider _timeProvider;

    public AgentHomeManifestService(IHostEnvironment hostEnvironment,
        IOptions<AgentHomeOptions> options,
        ISandboxRuntimeProvider sandboxProvider,
        TimeProvider timeProvider,
        ILogger<AgentHomeManifestService> logger)
    {
        ArgumentNullException.ThrowIfNull(hostEnvironment);
        ArgumentNullException.ThrowIfNull(options);
        _contentRootPath = hostEnvironment.ContentRootPath;
        _options = options.Value;
        _sandboxProvider = sandboxProvider ?? throw new ArgumentNullException(nameof(sandboxProvider));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<AgentHomeLayout> InitializeAsync(SandboxAttachKey attachKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attachKey);
        cancellationToken.ThrowIfCancellationRequested();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await InitializeCoreAsync(attachKey, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
    }

    private async Task<AgentHomeLayout> InitializeCoreAsync(SandboxAttachKey attachKey, CancellationToken cancellationToken)
    {
        var agentHomeRoot = ResolveAgentHomeRoot();
        Directory.CreateDirectory(agentHomeRoot);

        var existing = await TryReadManifestAsync(agentHomeRoot, cancellationToken).ConfigureAwait(false);
        var createdAt = existing?.CreatedAt;

        if (existing is not null)
        {
            if (!string.Equals(existing.OwnerUserId, attachKey.OwnerUserId, StringComparison.Ordinal))
            {
                await RecoverFromOwnerMismatchAsync(existing, agentHomeRoot, cancellationToken).ConfigureAwait(false);
                createdAt = null;
            }
            else if (existing.Version != AgentHomeManifest.CurrentVersion)
            {
                WipeAgentHome(agentHomeRoot);
                createdAt = null;
            }
            else if (existing.Status == AgentHomeStatus.Initializing && IsStale(existing))
            {
                _logger.LogWarning("AgentHome manifest was stuck initializing since {UpdatedAt}; reinitializing from scratch.",
                    existing.UpdatedAt);
                WipeAgentHome(agentHomeRoot);
                createdAt = null;
            }
            else if (existing.Status == AgentHomeStatus.Ready && IsLayoutComplete(agentHomeRoot))
            {
                return new AgentHomeLayout
                {
                    RootPath = agentHomeRoot,
                    Manifest = existing
                };
            }
        }

        return await MaterializeAsync(agentHomeRoot, attachKey, createdAt, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AgentHomeLayout> MaterializeAsync(string agentHomeRoot,
        SandboxAttachKey attachKey,
        DateTimeOffset? createdAt,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(agentHomeRoot);
        WriteLockFile(agentHomeRoot);

        var now = _timeProvider.GetUtcNow();
        var effectiveCreatedAt = createdAt ?? now;

        var initializing = BuildManifest(attachKey, AgentHomeStatus.Initializing, effectiveCreatedAt, now);
        await WriteManifestAtomicAsync(agentHomeRoot, initializing, cancellationToken).ConfigureAwait(false);

        EnsureDirectories(agentHomeRoot);
        await EnsureBaselineFilesAsync(agentHomeRoot, cancellationToken).ConfigureAwait(false);

        var ready = initializing with
        {
            Status = AgentHomeStatus.Ready,
            UpdatedAt = _timeProvider.GetUtcNow()
        };
        await WriteManifestAtomicAsync(agentHomeRoot, ready, cancellationToken).ConfigureAwait(false);

        RemoveLockFile(agentHomeRoot);

        return new AgentHomeLayout
        {
            RootPath = agentHomeRoot,
            Manifest = ready
        };
    }

    private async Task RecoverFromOwnerMismatchAsync(AgentHomeManifest existing,
        string agentHomeRoot,
        CancellationToken cancellationToken)
    {
        var priorKey = new SandboxAttachKey
        {
            OwnerUserId = existing.OwnerUserId,
            NodeId = existing.NodeId,
            ProviderName = existing.ProviderName,
            RuntimeProfile = existing.RuntimeProfile,
            ManifestVersion = existing.Version
        };

        try
        {
            var handle = await _sandboxProvider.ConnectAsync(priorKey, cancellationToken).ConfigureAwait(false);
            await _sandboxProvider.KillAsync(handle, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Killed prior-owner AgentHome sandbox on owner change for node {NodeId}.", existing.NodeId);
        }
        catch (SandboxHandleInvalidException)
        {
            // No live sandbox for the prior owner; nothing to kill.
        }

        WipeAgentHome(agentHomeRoot);
    }

    private string ResolveAgentHomeRoot()
    {
        var baseRoot = string.IsNullOrWhiteSpace(_options.RootPath)
            ? Path.Combine(_contentRootPath, DefaultRootDirectoryName)
            : _options.RootPath;
        return Path.Combine(baseRoot, AgentHomeDirectoryName);
    }

    private bool IsStale(AgentHomeManifest manifest)
    {
        var age = _timeProvider.GetUtcNow() - manifest.UpdatedAt;
        return age.TotalSeconds > _options.PrepareStaleAfterSeconds;
    }

    private static AgentHomeManifest BuildManifest(SandboxAttachKey key,
        AgentHomeStatus status,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        return new AgentHomeManifest
        {
            Version = AgentHomeManifest.CurrentVersion,
            Status = status,
            OwnerUserId = key.OwnerUserId,
            NodeId = key.NodeId,
            ProviderName = key.ProviderName,
            RuntimeProfile = key.RuntimeProfile,
            CreatedAt = createdAt,
            UpdatedAt = updatedAt
        };
    }

    private async Task<AgentHomeManifest?> TryReadManifestAsync(string agentHomeRoot, CancellationToken cancellationToken)
    {
        var path = Path.Combine(agentHomeRoot, ManifestFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<AgentHomeManifest>(stream, SerializerOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "AgentHome manifest could not be parsed; treating the layout as uninitialized.");
            return null;
        }
        catch (IOException exception)
        {
            _logger.LogWarning(exception, "AgentHome manifest could not be read; treating the layout as uninitialized.");
            return null;
        }
    }

    private static async Task WriteManifestAtomicAsync(string agentHomeRoot, AgentHomeManifest manifest, CancellationToken cancellationToken)
    {
        var path = Path.Combine(agentHomeRoot, ManifestFileName);
        var temporaryPath = path + ".tmp";
        var json = JsonSerializer.Serialize(manifest, SerializerOptions);
        await File.WriteAllTextAsync(temporaryPath, json, cancellationToken).ConfigureAwait(false);
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static void EnsureDirectories(string agentHomeRoot)
    {
        foreach (var relativePath in AgentHomeLayoutMap.Directories)
        {
            Directory.CreateDirectory(Path.Combine(agentHomeRoot, relativePath));
        }
    }

    private static async Task EnsureBaselineFilesAsync(string agentHomeRoot, CancellationToken cancellationToken)
    {
        foreach (var file in BuildBaselineFiles())
        {
            var fullPath = Path.Combine(agentHomeRoot, file.RelativePath);
            if (File.Exists(fullPath))
            {
                continue;
            }

            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(fullPath, file.Content, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsLayoutComplete(string agentHomeRoot)
    {
        var directoriesPresent = AgentHomeLayoutMap.Directories
                                                   .All(relativePath => Directory.Exists(Path.Combine(agentHomeRoot, relativePath)));
        var filesPresent = BuildBaselineFiles()
            .All(file => File.Exists(Path.Combine(agentHomeRoot, file.RelativePath)));
        return directoriesPresent && filesPresent;
    }

    private static IReadOnlyList<AgentHomeBaselineFile> BuildBaselineFiles()
    {
        var policyJson = JsonSerializer.Serialize(new AgentHomePolicy
            {
                Version = AgentHomePolicy.CurrentVersion,
                NetworkPolicy = "disabled",
                AllowReadOnlyMounts = false,
                WritableMounts = false
            },
            SerializerOptions);

        var skillsRegistry = JsonSerializer.Serialize(new
        {
            version = 1,
            skills = Array.Empty<string>()
        }, SerializerOptions);
        var toolsRegistry = JsonSerializer.Serialize(new
        {
            version = 1,
            tools = Array.Empty<string>()
        }, SerializerOptions);
        var toolsPolicy = JsonSerializer.Serialize(new
        {
            version = 1,
            writableMounts = false
        }, SerializerOptions);

        return
        [
            new AgentHomeBaselineFile(PolicyFileName, policyJson),
            new AgentHomeBaselineFile(ReadmeFileName, ReadmeContent),
            new AgentHomeBaselineFile(Path.Combine("skills", "registry.json"), skillsRegistry),
            new AgentHomeBaselineFile(Path.Combine("skills", "README.skills.md"), SkillsReadmeContent),
            new AgentHomeBaselineFile(Path.Combine("tools", "registry.json"), toolsRegistry),
            new AgentHomeBaselineFile(Path.Combine("tools", "policy.json"), toolsPolicy),
            new AgentHomeBaselineFile(Path.Combine("logs", "events.jsonl"), string.Empty),
            new AgentHomeBaselineFile(Path.Combine("logs", "commands.jsonl"), string.Empty),
            new AgentHomeBaselineFile(Path.Combine("logs", "tool-calls.jsonl"), string.Empty),
            new AgentHomeBaselineFile(Path.Combine("logs", "agent-events.jsonl"), string.Empty),
            new AgentHomeBaselineFile(Path.Combine("memory", "proposals", "node-memory.proposals.jsonl"), string.Empty),
            new AgentHomeBaselineFile(Path.Combine("memory", "proposals", "project-memory.proposals.jsonl"), string.Empty),
            new AgentHomeBaselineFile(Path.Combine("agents", "primary", "main", "plan.md"), PlanContent),
            new AgentHomeBaselineFile(Path.Combine("agents", "primary", "main", "scratchpad.md"), ScratchpadContent),
            new AgentHomeBaselineFile(Path.Combine("agents", "primary", "main", "findings.md"), FindingsContent)
        ];
    }

    private static void WipeAgentHome(string agentHomeRoot)
    {
        if (!agentHomeRoot.EndsWith(AgentHomeDirectoryName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Refusing to wipe a path that is not an agent-home root: '{agentHomeRoot}'.");
        }

        if (!Directory.Exists(agentHomeRoot))
        {
            return;
        }

        // Defense in depth: a recursive delete only ever fires on a directory that holds a materialized AgentHome
        // manifest. Wipe is reached solely after an existing manifest was read, so this invariant always holds; if it
        // does not, refuse rather than recursively delete an unexpected (mis-configured root) directory.
        if (!File.Exists(Path.Combine(agentHomeRoot, ManifestFileName)))
        {
            throw new InvalidOperationException($"Refusing to recursively delete '{agentHomeRoot}': no AgentHome manifest is present.");
        }

        Directory.Delete(agentHomeRoot, recursive: true);
    }

    private void WriteLockFile(string agentHomeRoot)
    {
        try
        {
            var path = Path.Combine(agentHomeRoot, LockFileName);
            File.WriteAllText(path, _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
        }
        catch (IOException exception)
        {
            _logger.LogDebug(exception, "AgentHome lock file could not be written.");
        }
        catch (UnauthorizedAccessException exception)
        {
            _logger.LogDebug(exception, "AgentHome lock file could not be written.");
        }
    }

    private void RemoveLockFile(string agentHomeRoot)
    {
        try
        {
            var path = Path.Combine(agentHomeRoot, LockFileName);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException exception)
        {
            _logger.LogDebug(exception, "AgentHome lock file could not be removed.");
        }
        catch (UnauthorizedAccessException exception)
        {
            _logger.LogDebug(exception, "AgentHome lock file could not be removed.");
        }
    }
}
