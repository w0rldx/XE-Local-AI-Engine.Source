namespace XE_Local_AI_Engine.Client.Services.ModelLab;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Models.Implementation;
using XE_Local_AI_Engine.Client.Services.Training.BaseArtifacts;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;
using XE_Local_AI_Engine.Client.Services.Training.Runs;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.HuggingFace.Contracts;
using XE_Local_AI_Engine.Providers.Training.Contracts;

/// <summary>Builds, caches and serves a model's PyTorch source index: the module tree and op graphs <c>modellab.py</c> reads on the meta device.</summary>
/// <remarks>
///     The job loads no weights, so it takes no GPU gate, lease or reservation, which a loaded model would refuse. It runs in the request,
///     one at a time, under a two-minute watchdog. The cache key holds the config's hash and the venv's transformers and torch versions.
///     A file is served only when listed, inside its package, with its hash. Accepted ceiling: no launch receipt, so a host that dies
///     mid-job leaves the script to finish alone; the startup reaper still deletes its work directory.
/// </remarks>
public sealed partial class ModelLabPyTorchSourceService
{
    /// <summary>The largest file served. Torch's biggest Python sources sit well under this.</summary>
    internal const int MaxSourceBytes = 4 * 1024 * 1024;

    private const string ConfigFileName = "config.json";
    private const string TorchPrefix = "torch:";

    private static readonly TimeSpan MaxJobDuration = TimeSpan.FromMinutes(2);

    private readonly IBaseCheckpointStore _checkpoints;
    private readonly INodeDataDirectory _dataDirectory;
    private readonly ITrainingRunEventBuffer _events;
    private readonly GgufInspectionService _gguf;
    private readonly ILogger<ModelLabPyTorchSourceService> _logger;
    private readonly ITrainingRuntimeService _runtime;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ModelLabScriptRunner _scripts;

    /// <summary>1 while a source job runs: single-flight, refused when busy, with no queue.</summary>
    private int _jobRunning;

    public ModelLabPyTorchSourceService(IServiceScopeFactory scopeFactory,
        IBaseCheckpointStore checkpoints,
        GgufInspectionService gguf,
        ITrainingRuntimeService runtime,
        ITrainingProcessSpawner spawner,
        ITrainingRunEventBuffer events,
        INodeDataDirectory dataDirectory,
        IOptions<ModelLabJobOptions> options,
        TimeProvider timeProvider,
        ILogger<ModelLabPyTorchSourceService> logger)
    {
        ArgumentNullException.ThrowIfNull(checkpoints);
        ArgumentNullException.ThrowIfNull(dataDirectory);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(gguf);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(spawner);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _checkpoints = checkpoints;
        _dataDirectory = dataDirectory;
        _events = events;
        _gguf = gguf;
        _logger = logger;
        _runtime = runtime;
        _scopeFactory = scopeFactory;
        var shared = options.Value;
        var maxDuration = shared.MaxDuration < MaxJobDuration ? shared.MaxDuration : MaxJobDuration;
        _scripts = new ModelLabScriptRunner(spawner, events, new ModelLabJobOptions
        {
            MaxDuration = maxDuration,
            InactivityTimeout = shared.InactivityTimeout < maxDuration ? shared.InactivityTimeout : maxDuration,
            ExitGracePeriod = shared.ExitGracePeriod
        }, timeProvider);
    }

    /// <summary>The job is Linux only, like the training runtime. A test swaps this to see the other platforms' answer.</summary>
    internal Func<bool> PlatformProbe { get; set; } = OperatingSystem.IsLinux;

    public bool IsPlatformSupported => PlatformProbe();

    public static string IndexRoot(INodeDataDirectory dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(dataDirectory);
        return Path.Combine(dataDirectory.Root, "model-lab", "pytorch-index");
    }

    /// <summary>The cached index for this config on the installed runtime, else a source job's. Every refusal is decided before the job starts.</summary>
    /// <exception cref="Models.InstalledGgufModelNotFoundException">No installed GGUF has that name.</exception>
    public async Task<ModelLabPyTorchOutcome> BuildAsync(ModelLabPyTorchBuildRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsPlatformSupported)
        {
            return Refuse(ModelLabPyTorchRefusal.UnsupportedPlatform,
                "The PyTorch source index runs on Linux only for now, because it needs the Python training runtime, which is available on Linux only.");
        }

        var scriptPath = Path.Combine(TrainingScripts.ResolveDirectory(), TrainingScripts.ModelLabScriptName);
        if (ResolvePackagesOrNull() is not { } packages || !File.Exists(scriptPath))
        {
            return Refuse(ModelLabPyTorchRefusal.RuntimeUnavailable, "The Python training runtime is not installed. Install it on the Training page, then build the index again.");
        }

        var modelName = request.ModelName.Trim();
        var descriptor = ModelLabArchitectureBuilder.Build(modelName, await _gguf.InspectAsync(modelName, ct));
        var config = request.Source == "checkpoint"
            ? await ReadCheckpointConfigAsync(request.BaseArtifactId.GetValueOrDefault(), ct)
            : await FetchHubConfigAsync(request.RepoId ?? string.Empty, request.Revision, ct);
        if (config.Refusal is not null)
        {
            return config.Refusal;
        }

        if (CheckConfigOrNull(config.Bytes) is { } unusable)
        {
            return unusable;
        }

        var indexId = IndexId(Convert.ToHexStringLower(SHA256.HashData(config.Bytes)), packages);
        var index = await ReadIndexOrNullAsync(indexId, ct);
        if (index is null)
        {
            if (Interlocked.Exchange(ref _jobRunning, 1) == 1)
            {
                return Refuse(ModelLabPyTorchRefusal.Busy, "Another PyTorch index is being built. Try again when it has finished.");
            }

            try
            {
                // The job that held the lock may have just built this very index.
                index = await ReadIndexOrNullAsync(indexId, ct);
                if (index is null)
                {
                    var built = await RunJobAsync(config.Bytes, packages, new ModelLabScriptRunner.ScriptCommand
                    {
                        Interpreter = packages.Interpreter,
                        ScriptPath = scriptPath
                    }, ct);
                    if (built.Refusal is not null)
                    {
                        return built.Refusal;
                    }

                    index = built.Index! with
                    {
                        DeclaredArchitectures = DeclaredArchitecturesOrNull(config.Bytes)
                    };
                    await SecureFilePermissions.WriteAllBytesAtomicAsync(IndexPath(indexId), JsonSerializer.SerializeToUtf8Bytes(index, TrainingJson.Options), CancellationToken.None);
                }
            }
            finally
            {
                Volatile.Write(ref _jobRunning, 0);
            }
        }

        var pointer = new ModelLabPyTorchPointer
        {
            IndexId = indexId,
            Source = config.Source!
        };
        TrainingRunWorkspace.CreateOwnerOnlyDirectory(Path.GetDirectoryName(PointerPath(modelName))!);
        await SecureFilePermissions.WriteAllBytesAtomicAsync(PointerPath(modelName), JsonSerializer.SerializeToUtf8Bytes(pointer, TrainingJson.Options), CancellationToken.None);
        return new ModelLabPyTorchOutcome
        {
            Index = Respond(modelName, pointer, index, descriptor)
        };
    }

    /// <summary>The last index built for this model, while the runtime that built it is still installed.</summary>
    /// <exception cref="ModelLabPyTorchIndexNotFoundException">None was built, or the runtime changed since.</exception>
    /// <exception cref="Models.InstalledGgufModelNotFoundException">No installed GGUF has that name.</exception>
    public async Task<ModelLabPyTorchIndexResponse> GetLastAsync(string modelName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        modelName = modelName.Trim();
        var descriptor = ModelLabArchitectureBuilder.Build(modelName, await _gguf.InspectAsync(modelName, ct));
        var pointer = await ReadJsonOrNullAsync<ModelLabPyTorchPointer>(PointerPath(modelName), ct);
        if (pointer is null
            || !IndexIdShape().IsMatch(pointer.IndexId)
            || !IsPlatformSupported
            || ResolvePackagesOrNull() is not { } packages
            || pointer.IndexId != IndexId(pointer.IndexId[..64], packages)
            || await ReadIndexOrNullAsync(pointer.IndexId, ct) is not { } index)
        {
            throw new ModelLabPyTorchIndexNotFoundException(modelName);
        }

        return Respond(modelName, pointer, index, descriptor);
    }

    /// <summary>One file an index names, read from the active runtime's package and checked against the index's hash.</summary>
    public async Task<ModelLabPyTorchSourceRead> ReadSourceAsync(string indexId, string file, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(indexId);
        ArgumentNullException.ThrowIfNull(file);
        if (!IsPlatformSupported)
        {
            return RefuseSource(ModelLabPyTorchRefusal.UnsupportedPlatform, "The PyTorch source index runs on Linux only for now.");
        }

        const string notListed = "That file is not part of this index.";
        var entry = IndexIdShape().IsMatch(indexId) && await ReadIndexOrNullAsync(indexId, ct) is { } index
            ? index.Files.FirstOrDefault(listed => string.Equals(listed.File, file, StringComparison.Ordinal))
            : null;
        if (entry is null)
        {
            return RefuseSource(ModelLabPyTorchRefusal.SourceNotInIndex, notListed);
        }

        if (ResolvePackagesOrNull() is not { } packages)
        {
            return RefuseSource(ModelLabPyTorchRefusal.RuntimeUnavailable, "The Python training runtime is not installed, so its source cannot be read.");
        }

        if (indexId != IndexId(indexId[..64], packages))
        {
            return RefuseSource(ModelLabPyTorchRefusal.SourceChanged, "The installed runtime changed since this index was built. Build the index again.");
        }

        var torch = file.StartsWith(TorchPrefix, StringComparison.Ordinal);
        var root = Path.Combine(packages.SitePackages, torch ? "torch" : "transformers");
        var relative = torch ? file[TorchPrefix.Length..] : file;
        var path = Path.Combine(root, relative);
        // The index came from a script, so its names are checked like any input: a relative Python file, lexically inside the package, no link.
        if (relative.Length == 0 || Path.IsPathRooted(relative) || relative.Contains('\\', StringComparison.Ordinal)
            || !relative.EndsWith(".py", StringComparison.Ordinal) || !PathContainment.IsUnderRoot(path, root))
        {
            return RefuseSource(ModelLabPyTorchRefusal.SourceNotInIndex, notListed);
        }

        var info = new FileInfo(Path.GetFullPath(path));
        if (!info.Exists || info.LinkTarget is not null)
        {
            ForgetIndex(indexId);
            return RefuseSource(ModelLabPyTorchRefusal.SourceChanged, "That file is no longer in the installed runtime as indexed. Build the index again.");
        }

        if (info.Length > MaxSourceBytes)
        {
            return RefuseSource(ModelLabPyTorchRefusal.SourceTooLarge, "That file is too large to show.");
        }

        var bytes = await File.ReadAllBytesAsync(info.FullName, ct);
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!string.Equals(sha256, entry.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            ForgetIndex(indexId);
            return RefuseSource(ModelLabPyTorchRefusal.SourceChanged, "That file changed since the index was built. Build the index again.");
        }

        return new ModelLabPyTorchSourceRead
        {
            Source = new ModelLabPyTorchSourceFile
            {
                File = file,
                Sha256 = sha256,
                Text = Encoding.UTF8.GetString(bytes)
            }
        };
    }

    private async Task<ConfigRead> ReadCheckpointConfigAsync(Guid artifactId, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        BaseArtifactView artifact;
        try
        {
            artifact = await scope.ServiceProvider.GetRequiredService<IBaseArtifactService>().GetAsync(artifactId, ct);
        }
        catch (TrainingNotFoundException)
        {
            return ConfigRefused(ModelLabPyTorchRefusal.CheckpointNotFound, "The checkpoint was not found.");
        }

        if (artifact.Status != nameof(TrainingBaseArtifactStatus.Ready))
        {
            return ConfigRefused(ModelLabPyTorchRefusal.CheckpointNotReady, "The checkpoint has not finished downloading.");
        }

        var config = artifact.Files.FirstOrDefault(static file => string.Equals(file.FileName, ConfigFileName, StringComparison.OrdinalIgnoreCase));
        var info = config is null ? null : new FileInfo(config.LocalPath);
        if (info is not { Exists: true })
        {
            return ConfigRefused(ModelLabPyTorchRefusal.ConfigMissing, "The checkpoint has no config.json.");
        }

        if (info.Length > BaseCheckpointConfigException.MaxConfigBytes)
        {
            return ConfigRefused(ModelLabPyTorchRefusal.ConfigTooLarge, "The checkpoint's config.json is over 1 MiB.");
        }

        return new ConfigRead
        {
            Bytes = await File.ReadAllBytesAsync(info.FullName, ct),
            Source = new ModelLabPyTorchConfigSource
            {
                Kind = "checkpoint",
                RepoId = artifact.RepoId,
                Revision = artifact.Revision,
                BaseArtifactId = artifact.Id
            }
        };
    }

    /// <summary>Only <c>config.json</c>, pinned to the commit the Hub resolves and kept under that commit. No weights, and no safetensors needed.</summary>
    private async Task<ConfigRead> FetchHubConfigAsync(string repoId, string? revision, CancellationToken ct)
    {
        var staging = Path.Combine(ModelLabExperimentService.WorkRoot(_dataDirectory), Guid.NewGuid().ToString("N"));
        try
        {
            var manifest = await _checkpoints.FetchConfigAsync(repoId.Trim(), string.IsNullOrWhiteSpace(revision) ? null : revision.Trim(), staging, ct);
            var info = new FileInfo(manifest.Files[0].LocalPath);
            if (info.Length > BaseCheckpointConfigException.MaxConfigBytes)
            {
                return ConfigRefused(ModelLabPyTorchRefusal.ConfigTooLarge, $"The config.json of {manifest.RepoId} is over 1 MiB.");
            }

            var bytes = await File.ReadAllBytesAsync(info.FullName, ct);
            var kept = Path.Combine(IndexRoot(_dataDirectory), "configs", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{manifest.RepoId}@{manifest.Revision}"))));
            TrainingRunWorkspace.CreateOwnerOnlyDirectory(kept);
            await SecureFilePermissions.WriteAllBytesAtomicAsync(Path.Combine(kept, ConfigFileName), bytes, CancellationToken.None);
            return new ConfigRead
            {
                Bytes = bytes,
                Source = new ModelLabPyTorchConfigSource
                {
                    Kind = "huggingface",
                    RepoId = manifest.RepoId,
                    Revision = manifest.Revision
                }
            };
        }
        catch (BaseCheckpointConfigException exception)
        {
            return ConfigRefused(exception.Failure switch
            {
                BaseCheckpointConfigFailure.ConfigMissing => ModelLabPyTorchRefusal.ConfigMissing,
                BaseCheckpointConfigFailure.ConfigTooLarge => ModelLabPyTorchRefusal.ConfigTooLarge,
                _ => ModelLabPyTorchRefusal.RepoUnreachable
            }, exception.Message);
        }
        catch (HuggingFaceDownloadException exception)
        {
            return ConfigRefused(exception.Reason is HuggingFaceDownloadFailure.Gated or HuggingFaceDownloadFailure.Unauthorized ? ModelLabPyTorchRefusal.RepoGated : ModelLabPyTorchRefusal.RepoUnreachable,
                exception.Message);
        }
        finally
        {
            BestEffortFile.TryDeleteDirectory(staging);
        }
    }

    /// <summary>Read as JSON before any script sees it: a config that names its own code is refused here, and the script refuses it again.</summary>
    private static ModelLabPyTorchOutcome? CheckConfigOrNull(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Refuse(ModelLabPyTorchRefusal.ConfigUnreadable, "The config.json is not a JSON object.");
            }

            var remoteCode = root.TryGetProperty("auto_map", out _)
                             || (root.TryGetProperty("text_config", out var text) && text.ValueKind == JsonValueKind.Object && text.TryGetProperty("auto_map", out _));
            return remoteCode
                ? Refuse(ModelLabPyTorchRefusal.RemoteCode, "This model ships its own modeling code (auto_map). Model Lab never runs a repository's code, so it is not indexed.")
                : null;
        }
        catch (JsonException)
        {
            return Refuse(ModelLabPyTorchRefusal.ConfigUnreadable, "The config.json is not valid JSON.");
        }
    }

    /// <summary>The config's <c>architectures</c> strings, or null when it names none. The config was already checked to be a JSON object.</summary>
    private static List<string>? DeclaredArchitecturesOrNull(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        if (!document.RootElement.TryGetProperty("architectures", out var declared) || declared.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var names = declared.EnumerateArray().Where(static name => name.ValueKind == JsonValueKind.String).Select(static name => name.GetString()!).ToList();
        return names.Count == 0 ? null : names;
    }

    private async Task<JobBuild> RunJobAsync(byte[] config, RuntimePackages packages, ModelLabScriptRunner.ScriptCommand script, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var workDirectory = Path.Combine(ModelLabExperimentService.WorkRoot(_dataDirectory), id.ToString("N"));
        var configDirectory = Path.Combine(workDirectory, "config");
        var job = new ModelLabSourceJobV1
        {
            ConfigDirectory = configDirectory,
            ResultPath = Path.Combine(workDirectory, "result.json")
        };
        try
        {
            TrainingRunWorkspace.CreateOwnerOnlyDirectory(Path.GetDirectoryName(workDirectory)!);
            TrainingRunWorkspace.CreateOwnerOnlyDirectory(workDirectory);
            TrainingRunWorkspace.CreateOwnerOnlyDirectory(configDirectory);
            await File.WriteAllBytesAsync(Path.Combine(configDirectory, ConfigFileName), config, CancellationToken.None);
            var run = await _scripts.RunAsync(id, workDirectory, JsonSerializer.SerializeToUtf8Bytes(job, TrainingJson.Options), script, static _ => Task.CompletedTask, ct);
            if (FailureOrNull(run, ct.IsCancellationRequested) is { } failed)
            {
                return new JobBuild
                {
                    Refusal = failed
                };
            }

            var result = new FileInfo(job.ResultPath);
            if (!result.Exists)
            {
                return JobRefused(ModelLabPyTorchRefusal.IndexFailed, "The Model Lab script finished without writing its index.");
            }

            // The script holds the cap itself. This is the check that does not depend on the script being right.
            if (result.Length > job.MaxResultBytes)
            {
                return JobRefused(ModelLabPyTorchRefusal.ResultTooLarge, "The index is larger than its byte cap, so nothing was kept.");
            }

            var index = await ReadJsonOrNullAsync<ModelLabPyTorchIndex>(job.ResultPath, CancellationToken.None);
            if (index is null)
            {
                return JobRefused(ModelLabPyTorchRefusal.IndexFailed, "The Model Lab script wrote an index this build cannot read.");
            }

            // The cache key names the runtime the index was read from; an index from another one must not be filed under it.
            if (index.TransformersVersion != packages.TransformersVersion || index.TorchVersion != packages.TorchVersion)
            {
                return JobRefused(ModelLabPyTorchRefusal.IndexFailed, "The script ran on other transformers or torch versions than the installed runtime names.");
            }

            return new JobBuild
            {
                Index = index
            };
        }
        finally
        {
            BestEffortFile.TryDeleteDirectory(workDirectory);
            _events.EvictPlaintext(id);
        }
    }

    private static ModelLabPyTorchOutcome? FailureOrNull(ModelLabScriptRunner.ScriptRun run, bool cancelRequested)
    {
        if (cancelRequested || run.State.ScriptCancelled || run.ExitCode == TrainingRunExecutor.CancelledExitCode)
        {
            return Refuse(ModelLabPyTorchRefusal.IndexFailed, "The index build was stopped.");
        }

        if (run.FailureReason is { } reason)
        {
            return Refuse(ModelLabPyTorchRefusal.IndexFailed, reason);
        }

        if (run.State.ErrorCategory is { } category)
        {
            var message = run.State.ErrorMessage ?? category;
            return category switch
            {
                "remote_code" => Refuse(ModelLabPyTorchRefusal.RemoteCode, message),
                "unsupported_model_type" => Refuse(ModelLabPyTorchRefusal.UnsupportedModelType, message),
                "config_unreadable" => Refuse(ModelLabPyTorchRefusal.ConfigUnreadable, message),
                "no_blocks" => Refuse(ModelLabPyTorchRefusal.NoBlocks, message),
                "result_too_large" => Refuse(ModelLabPyTorchRefusal.ResultTooLarge, message),
                _ => Refuse(ModelLabPyTorchRefusal.IndexFailed, run.State.WithLastOutput(message))
            };
        }

        return !run.State.HandshakeSeen || !run.State.Done || run.ExitCode != 0
            ? Refuse(ModelLabPyTorchRefusal.IndexFailed, run.State.WithLastOutput($"The Model Lab script exited with status {run.ExitCode}."))
            : null;
    }

    /// <summary>The active venv's site-packages and the versions its <c>dist-info</c> names; null when the runtime is not Ready or not laid out as a venv.</summary>
    private RuntimePackages? ResolvePackagesOrNull()
    {
        if (_runtime.GetStatus().Phase != TrainingRuntimePhase.Ready || _runtime.ResolveInterpreterPath() is not { } interpreter)
        {
            return null;
        }

        // <venv>/bin/python: the marker file keeps a stray interpreter path from naming some other directory's packages.
        var venv = Path.GetDirectoryName(Path.GetDirectoryName(interpreter));
        if (venv is null || !File.Exists(Path.Combine(venv, "pyvenv.cfg")) || !Directory.Exists(Path.Combine(venv, "lib")))
        {
            return null;
        }

        foreach (var python in Directory.EnumerateDirectories(Path.Combine(venv, "lib"), "python*"))
        {
            var sitePackages = Path.Combine(python, "site-packages");
            if (DistVersionOrNull(sitePackages, "transformers") is { } transformers && DistVersionOrNull(sitePackages, "torch") is { } torch)
            {
                return new RuntimePackages
                {
                    Interpreter = interpreter,
                    SitePackages = sitePackages,
                    TransformersVersion = transformers,
                    TorchVersion = torch
                };
            }
        }

        return null;
    }

    /// <summary><c>torch-2.13.0+cpu.dist-info</c> → <c>2.13.0+cpu</c>, when exactly one such directory and the package beside it exist.</summary>
    private static string? DistVersionOrNull(string sitePackages, string package)
    {
        if (!Directory.Exists(Path.Combine(sitePackages, package)))
        {
            return null;
        }

        var found = Directory.GetDirectories(sitePackages, package + "-*.dist-info");
        if (found.Length != 1)
        {
            return null;
        }

        var name = Path.GetFileName(found[0]);
        var version = name[(package.Length + 1)..^".dist-info".Length];
        return VersionShape().IsMatch(version) ? version : null;
    }

    private static string IndexId(string configSha256, RuntimePackages packages) =>
        $"{configSha256}-{packages.TransformersVersion}-{packages.TorchVersion}-{ModelLabTraceSpec.ContractVersion}";

    private string IndexPath(string indexId)
    {
        var root = IndexRoot(_dataDirectory);
        TrainingRunWorkspace.CreateOwnerOnlyDirectory(root);
        return Path.Combine(root, indexId + ".json");
    }

    private string PointerPath(string modelName) =>
        Path.Combine(IndexRoot(_dataDirectory), "models", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(modelName))) + ".json");

    /// <summary>
    ///     A listed file that changed under the same runtime versions leaves the cache key as it was, so the next build would serve the
    ///     stale index again: dropping it makes "build the index again" run the job.
    /// </summary>
    private void ForgetIndex(string indexId) =>
        BestEffortFile.TryDeleteFile(Path.Combine(IndexRoot(_dataDirectory), indexId + ".json"));

    private Task<ModelLabPyTorchIndex?> ReadIndexOrNullAsync(string indexId, CancellationToken ct) =>
        ReadJsonOrNullAsync<ModelLabPyTorchIndex>(Path.Combine(IndexRoot(_dataDirectory), indexId + ".json"), ct);

    /// <summary>Null when the file is missing, over the result cap or unreadable: a damaged cache entry is rebuilt, not served.</summary>
    private async Task<T?> ReadJsonOrNullAsync<T>(string path, CancellationToken ct)
        where T : class
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > ModelLabTraceSpec.MaxResultBytes)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(await File.ReadAllBytesAsync(path, ct), TrainingJson.Options);
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "A Model Lab PyTorch index file could not be read and is ignored.");
            return null;
        }
    }

    private static ModelLabPyTorchIndexResponse Respond(string modelName, ModelLabPyTorchPointer pointer, ModelLabPyTorchIndex index, ModelLabArchitectureResponse descriptor) =>
        new()
        {
            IndexId = pointer.IndexId,
            ModelName = modelName,
            Source = pointer.Source,
            Index = index,
            Join = ModelLabPyTorchJoiner.Join(descriptor, index)
        };

    private static ModelLabPyTorchOutcome Refuse(ModelLabPyTorchRefusal refusal, string message) =>
        new()
        {
            Refusal = refusal,
            RefusalMessage = message
        };

    private static ModelLabPyTorchSourceRead RefuseSource(ModelLabPyTorchRefusal refusal, string message) =>
        new()
        {
            Refusal = refusal,
            RefusalMessage = message
        };

    private static ConfigRead ConfigRefused(ModelLabPyTorchRefusal refusal, string message) =>
        new()
        {
            Refusal = Refuse(refusal, message)
        };

    private static JobBuild JobRefused(ModelLabPyTorchRefusal refusal, string message) =>
        new()
        {
            Refusal = Refuse(refusal, message)
        };

    [GeneratedRegex("^[0-9A-Za-z.+_]+$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex VersionShape();

    [GeneratedRegex("^[0-9a-f]{64}-[0-9A-Za-z.+_]+-[0-9A-Za-z.+_]+-[0-9]+$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex IndexIdShape();

    private sealed class ConfigRead
    {
        public byte[] Bytes { get; init; } = [];

        public ModelLabPyTorchConfigSource? Source { get; init; }

        public ModelLabPyTorchOutcome? Refusal { get; init; }
    }

    private sealed class JobBuild
    {
        public ModelLabPyTorchIndex? Index { get; init; }

        public ModelLabPyTorchOutcome? Refusal { get; init; }
    }

    private sealed class RuntimePackages
    {
        public required string Interpreter { get; init; }

        public required string SitePackages { get; init; }

        public required string TransformersVersion { get; init; }

        public required string TorchVersion { get; init; }
    }
}
