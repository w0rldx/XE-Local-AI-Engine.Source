namespace XE_Local_AI_Engine.Client.Services.NodeSettings.Implementation;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Services.Containers;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;

/// <summary>
///     Persistence boundary for node settings data.
/// </summary>
public sealed class NodeSettingsStore : INodeSettingsStore, IDisposable
{
    private const string SettingsFileName = "node-settings.json";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim _lock = new(initialCount: 1, maxCount: 1);
    private readonly ILogger<NodeSettingsStore> _logger;
    private readonly string _settingsPath;

    public NodeSettingsStore(INodeDataDirectory dataDirectory, ILogger<NodeSettingsStore> logger)
    {
        ArgumentNullException.ThrowIfNull(dataDirectory);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _settingsPath = Path.Combine(dataDirectory.Root, SettingsFileName);
    }

    public void Dispose()
    {
        _lock.Dispose();
    }

    public async Task<StoredNodeSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            return await LoadUnlockedAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<StoredNodeSettings?> LoadStrictAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            return await ReadUnlockedAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Synchronous twin of <see cref="LoadAsync" /> for the composition/startup path.</summary>
    /// <remarks>
    ///     Uses a synchronous lock + file read so DI factory seeds and singleton constructors never block on async file
    ///     I/O (which starves the thread pool during host startup). Same tolerant-deserialize + <see cref="Normalize" />
    ///     semantics as the async path.
    /// </remarks>
    public StoredNodeSettings Load(CancellationToken cancellationToken = default)
    {
        _lock.Wait(cancellationToken);
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return new StoredNodeSettings();
            }

            try
            {
                var json = File.ReadAllText(_settingsPath);
                var settings = JsonSerializer.Deserialize<StoredNodeSettings>(json, SerializerOptions);
                return Normalize(settings ?? new StoredNodeSettings());
            }
            catch (JsonException exception)
            {
                _logger.LogWarning(exception, "Node settings could not be deserialized. Falling back to defaults.");
                return new StoredNodeSettings();
            }
            catch (IOException exception)
            {
                _logger.LogWarning(exception, "Node settings could not be read. Falling back to defaults.");
                return new StoredNodeSettings();
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(StoredNodeSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalizedSettings = Normalize(settings);

        await _lock.WaitAsync(cancellationToken);
        try
        {
            await SaveUnlockedAsync(normalizedSettings, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Recovery from an unreadable <c>node-settings.json</c> is an explicit operator action, never an automatic
    ///     overwrite: this path throws <see cref="NodeSettingsUnreadableException" /> instead. Automatic callers
    ///     (<c>ExternalProviderStartupReconciler</c> first) already swallow a startup failure.
    /// </remarks>
    public async Task<StoredNodeSettings> UpdateAsync(Func<StoredNodeSettings, StoredNodeSettings> mutate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        // ONE lock acquisition around load-mutate-save: Load and Save each take the lock on their own, so a caller composing
        // them holds it for neither gap between — and this file is written whole, so a concurrent writer's fields are lost there, not merged.
        await _lock.WaitAsync(cancellationToken);
        try
        {
            // STRICT, unlike LoadAsync: a read-modify-write over an unreadable file would mutate a DEFAULT record and persist it as valid,
            // healing corruption into a settings file that has silently lost every stored value — the node's external-access posture included.
            var current = await ReadUnlockedAsync(cancellationToken);
            if (current is null)
            {
                _logger.LogError("Node settings at {SettingsPath} are present but unreadable; nothing was written. Repair or delete node-settings.json to recover.", _settingsPath);
                throw new NodeSettingsUnreadableException(_settingsPath);
            }

            var mutated = Normalize(mutate(current) ?? throw new InvalidOperationException("The node-settings mutation returned null."));
            await SaveUnlockedAsync(mutated, cancellationToken);
            return mutated;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    ///     The TOLERANT read every ordinary consumer wants: an unreadable file degrades to the default record so one
    ///     corrupt byte cannot take a feature down. <see cref="ReadUnlockedAsync" /> is the strict twin.
    /// </summary>
    private async Task<StoredNodeSettings> LoadUnlockedAsync(CancellationToken cancellationToken)
    {
        return await ReadUnlockedAsync(cancellationToken) ?? new StoredNodeSettings();
    }

    /// <summary>The STRICT read, and the only place that can tell the three cases apart.</summary>
    /// <remarks>
    ///     A missing file is the DEFAULT record (a legacy install that never saved, not an error), a readable file is
    ///     the normalized record, and a present but unreadable file is <see langword="null" />. Callers must not
    ///     conflate the last two — the difference between "no value yet" and "I cannot read the value" is what stops
    ///     the boot backfill deciding an external-access posture on the strength of a corrupt file.
    /// </remarks>
    private async Task<StoredNodeSettings?> ReadUnlockedAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_settingsPath))
        {
            return new StoredNodeSettings();
        }

        try
        {
            await using var fileStream = File.OpenRead(_settingsPath);
            var settings = await JsonSerializer.DeserializeAsync<StoredNodeSettings>(fileStream, SerializerOptions, cancellationToken);
            return Normalize(settings ?? new StoredNodeSettings());
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "Node settings could not be deserialized.");
            return null;
        }
        catch (IOException exception)
        {
            _logger.LogWarning(exception, "Node settings could not be read.");
            return null;
        }
    }

    /// <summary>
    ///     Writes through a temp SIBLING plus an atomic rename, so a crash mid-write never leaves a torn file: the old
    ///     one survives intact.
    /// </summary>
    /// <remarks>
    ///     <see cref="File.Move(string, string, bool)" /> within one directory is a rename — atomic on Linux (<c>rename(2)</c>) and
    ///     Windows (<c>MoveFileEx</c> / <c>MOVEFILE_REPLACE_EXISTING</c>); same-directory is load-bearing, a rename across filesystems
    ///     is a copy, not a replace. The Windows caveat recorded for <c>llama-launch-fallback.json</c> — an atomic replace over a file
    ///     another holder has open FAILS on Windows, which is why that file's lock sits on a sibling <c>.lock</c> — does not bite here,
    ///     so do not "fix" it: every read and write here runs under <c>_lock</c>, the write opens <see cref="FileShare.None" />, and one node instance runs per data directory.
    /// </remarks>
    private async Task SaveUnlockedAsync(StoredNodeSettings normalizedSettings, CancellationToken cancellationToken)
    {
        var tempPath = string.Concat(_settingsPath, ".", Guid.NewGuid().ToString("N"), ".tmp");
        try
        {
            // Create with 0600 up front on non-Windows so the file is never briefly world-readable between create and chmod, and so the
            // RENAMED file keeps owner-only permissions. Windows relies on the per-user data-directory ACL (UnixCreateMode is unsupported there).
            await using (var fileStream = CreateOwnerOnly(tempPath))
            {
                await JsonSerializer.SerializeAsync(fileStream, normalizedSettings, SerializerOptions, cancellationToken);
            }

            File.Move(tempPath, _settingsPath, overwrite: true);
        }
        catch
        {
            // A failed serialize or move leaves the previous file untouched; drop the partial temp so no litter builds up. Best-effort, in its
            // own try: a delete that throws here would REPLACE the original failure with a cleanup error, hiding why the write failed.
            DeleteIfExists(tempPath);
            throw;
        }
    }

    /// <summary>
    ///     Best-effort temp-file cleanup, matching <c>KnowledgeDocumentBlobStore.DeleteFileIfExists</c>: a leftover
    ///     <c>.tmp</c> sibling is litter, never a correctness problem, and must not mask the exception being propagated.
    /// </summary>
    private static void DeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup; a transient IO error leaves an orphan temp file next to node-settings.json.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup; a permission error leaves an orphan temp file next to node-settings.json.
        }
    }

    /// <summary>
    ///     Opens a truncating write stream for <paramref name="path" />.
    /// </summary>
    /// <remarks>
    ///     On non-Windows the file is created with owner-only (0600) permissions atomically via
    ///     <see cref="FileStreamOptions.UnixCreateMode" />, matching the key-file posture. On Windows that option is
    ///     unsupported, so a plain create is used and the per-user data-directory ACL governs access.
    /// </remarks>
    private static FileStream CreateOwnerOnly(string path)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None
        };

        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return new FileStream(path, options);
    }

    /// <summary>Clamps/validates each stored field independently.</summary>
    /// <remarks>
    ///     An out-of-range timeout resets only that one field to its documented default, never discarding the other
    ///     migrated fields. Each newer nullable field falls back to <see langword="null" /> when out-of-range or
    ///     malformed so the accessor re-seeds it, preserving the stored &gt; seed &gt; default precedence.
    /// </remarks>
    private static StoredNodeSettings Normalize(StoredNodeSettings settings)
    {
        // Clamp the timeout in isolation: one corrupt/out-of-range value in a hand-edited file must not wipe the rest.
        var timeout = settings.MaxMessageRequestTimeoutSeconds is < StoredNodeSettings.MinMaxMessageRequestTimeoutSeconds or > StoredNodeSettings.MaxMaxMessageRequestTimeoutSeconds
            ? StoredNodeSettings.DefaultMaxMessageRequestTimeoutSeconds
            : settings.MaxMessageRequestTimeoutSeconds;

        return settings with
        {
            MaxMessageRequestTimeoutSeconds = timeout,
            DefaultModelName = TrimToNull(settings.DefaultModelName),
            ToolCapableModels = NormalizeStringList(settings.ToolCapableModels),
            OllamaEndpoint = NormalizeAbsoluteUrl(settings.OllamaEndpoint),
            HuggingFaceDefaultQuant = TrimToNull(settings.HuggingFaceDefaultQuant),
            HuggingFaceDiskMarginBytes = ClampPositiveLong(settings.HuggingFaceDiskMarginBytes),
            LlamaMaxLoadedProcesses = ClampToRange(settings.LlamaMaxLoadedProcesses,
                StoredNodeSettings.MinLlamaMaxLoadedProcesses, StoredNodeSettings.MaxLlamaMaxLoadedProcesses),
            LlamaIdleTimeToLiveSeconds = ClampToRange(settings.LlamaIdleTimeToLiveSeconds,
                StoredNodeSettings.MinLlamaIdleTimeToLiveSeconds, StoredNodeSettings.MaxLlamaIdleTimeToLiveSeconds),
            TranscriptionSelectedModelId = TrimToNull(settings.TranscriptionSelectedModelId),
            TranscriptionIdleTimeoutMinutes = ClampToRange(settings.TranscriptionIdleTimeoutMinutes,
                StoredNodeSettings.MinTranscriptionIdleTimeoutMinutes,
                StoredNodeSettings.MaxTranscriptionIdleTimeoutMinutes),
            KeepModelWarmModelName = TrimToNull(settings.KeepModelWarmModelName),
            KeepModelWarmIntervalSeconds = ClampToRange(settings.KeepModelWarmIntervalSeconds,
                StoredNodeSettings.MinKeepModelWarmIntervalSeconds, StoredNodeSettings.MaxKeepModelWarmIntervalSeconds),
            MaxResponseSizeMb = ClampToRange(settings.MaxResponseSizeMb,
                StoredNodeSettings.MinMaxResponseSizeMb, StoredNodeSettings.MaxMaxResponseSizeMb),
            RecommendedLlamaCppTag = NormalizeRecommendedTag(settings.RecommendedLlamaCppTag),
            OrchestrationIdleTimeoutSeconds = ClampToRange(settings.OrchestrationIdleTimeoutSeconds,
                StoredNodeSettings.MinOrchestrationIdleTimeoutSeconds, StoredNodeSettings.MaxOrchestrationIdleTimeoutSeconds),
            AgentHomePrepareTimeoutSeconds = ClampToRange(settings.AgentHomePrepareTimeoutSeconds,
                StoredNodeSettings.MinAgentHomeTimeoutSeconds, StoredNodeSettings.MaxAgentHomeTimeoutSeconds),
            AgentHomeCommandTimeoutSeconds = ClampToRange(settings.AgentHomeCommandTimeoutSeconds,
                StoredNodeSettings.MinAgentHomeTimeoutSeconds, StoredNodeSettings.MaxAgentHomeTimeoutSeconds),
            AgentHomeMaxSelectedFolderBytes = ClampPositiveLong(settings.AgentHomeMaxSelectedFolderBytes),
            AgentHomeMaxPatchBytes = ClampPositiveLong(settings.AgentHomeMaxPatchBytes),
            MaxPendingToolCallAgeMinutes = ClampToRange(settings.MaxPendingToolCallAgeMinutes,
                StoredNodeSettings.MinMaxPendingToolCallAgeMinutes, StoredNodeSettings.MaxMaxPendingToolCallAgeMinutes),
            DetachedGraceSeconds = NormalizeDetachedGraceSeconds(settings.DetachedGraceSeconds),
            ChatCacheReuse = ClampToRange(settings.ChatCacheReuse,
                StoredNodeSettings.MinChatCacheReuse, StoredNodeSettings.MaxChatCacheReuse),
            SpeculativeMode = NormalizeSpeculativeMode(settings.SpeculativeMode),
            KvCacheType = NormalizeKvCacheType(settings.KvCacheType),
            ContainerRuntimeSelection = NormalizeContainerRuntimeSelection(settings.ContainerRuntimeSelection),
            SpeculativeDraftModelName = TrimToNull(settings.SpeculativeDraftModelName),
            SpeculativeDraftMaxTokens = ClampToRange(settings.SpeculativeDraftMaxTokens,
                StoredNodeSettings.MinSpeculativeDraftMaxTokens, StoredNodeSettings.MaxSpeculativeDraftMaxTokens),
            SpeculativeDraftGpuLayers = ClampToRange(settings.SpeculativeDraftGpuLayers,
                StoredNodeSettings.MinSpeculativeDraftGpuLayers, StoredNodeSettings.MaxSpeculativeDraftGpuLayers),
            RerankerModelName = TrimToNull(settings.RerankerModelName),
            AutoEffortFastModelName = TrimToNull(settings.AutoEffortFastModelName),
            DefaultVoiceProfile = TrimToNull(settings.DefaultVoiceProfile),
            UsageRates = NormalizeUsageRates(settings.UsageRates),
            ExternalAccessProfile = NormalizeExternalAccessProfile(settings.ExternalAccessProfile),
            UiMode = NormalizeUiMode(settings.UiMode),
            UpdateChannel = NormalizeUpdateChannel(settings.UpdateChannel)
        };
    }

    /// <summary>The persistence authority for usage-rate hygiene.</summary>
    /// <remarks>
    ///     Drops entries with a blank model name or a negative / non-finite rate (via <see cref="ModelRate.HasValidRates" /> — one shared
    ///     predicate with the boundary validator and the resolver), trims the model-name keys, and de-duplicates them case-insensitively
    ///     (matching how the resolver and the run-envelope <c>ModelName</c> compare). An empty result collapses to
    ///     <see langword="null" /> so an all-junk map reads as "no override".
    /// </remarks>
    private static NodeUsageRateSettings? NormalizeUsageRates(NodeUsageRateSettings? rates)
    {
        if (rates?.Models is not { Count: > 0 } models)
        {
            return null;
        }

        var cleaned = new Dictionary<string, ModelRate>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, rate) in models)
        {
            var trimmed = TrimToNull(name);
            if (trimmed is null || rate is null || !rate.HasValidRates)
            {
                continue;
            }

            cleaned[trimmed] = rate;
        }

        return cleaned.Count == 0
            ? null
            : new NodeUsageRateSettings
            {
                Models = cleaned
            };
    }

    private static string? TrimToNull(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>
    ///     Unlike every other numeric field, a NEGATIVE disconnect grace clamps to <c>0</c> rather than re-seeding.
    /// </summary>
    /// <remarks>
    ///     <c>0</c> is a meaningful value here (never cancel), so "the operator asked for no reaping, badly" is a
    ///     clearer reading of a negative than "fall back to 300 s and reap anyway". An absurdly LARGE value still
    ///     re-seeds like the rest.
    /// </remarks>
    private static int? NormalizeDetachedGraceSeconds(int? value)
    {
        return value switch
        {
            null => null,
            < StoredNodeSettings.MinDetachedGraceSeconds => StoredNodeSettings.MinDetachedGraceSeconds,
            > StoredNodeSettings.MaxDetachedGraceSeconds => null,
            _ => value
        };
    }

    private static int? ClampToRange(int? value, int min, int max)
    {
        if (value is null)
        {
            return null;
        }

        return value < min || value > max ? null : value;
    }

    private static long? ClampPositiveLong(long? value)
    {
        if (value is null)
        {
            return null;
        }

        return value <= 0 ? null : value;
    }

    /// <summary>
    ///     Blank or absent is the LEGACY install — nobody ever wrote a profile — so it stays <see langword="null" />
    ///     and the boot backfill may stamp it.
    /// </summary>
    /// <remarks>
    ///     A non-blank string the engine does not recognise is the opposite case: somebody wrote a profile, just not one this engine
    ///     knows, so the node is ASKED AGAIN rather than answered for — it loads as
    ///     <see cref="StoredNodeSettings.ExternalAccessProfilePending" />. The gated services wait on <c>pending</c>, the backfill leaves
    ///     any non-null profile alone, the three switches beside it are kept exactly as they are, and the SPA shows the profile chooser
    ///     to the administrator at the next login. The comparison is ordinal, so <c>"Offline"</c> is unrecognised and <c>"  offline  "</c> is not.
    /// </remarks>
    private static string? NormalizeExternalAccessProfile(string? value)
    {
        var trimmed = TrimToNull(value);

        if (trimmed is null)
        {
            return null;
        }

        return StoredNodeSettings.IsValidExternalAccessProfile(trimmed) ? trimmed : StoredNodeSettings.ExternalAccessProfilePending;
    }

    /// <summary>
    ///     Unlike the external-access profile, an unrecognised value falls back to <see langword="null" /> rather than
    ///     to an "ask again" literal.
    /// </summary>
    /// <remarks>
    ///     The mode is presentation only, so a junk value costs nothing to answer for — the reader re-seeds
    ///     <see cref="StoredNodeSettings.DefaultUiMode" /> and the navigation looks exactly as it did before the setting existed. The
    ///     comparison is ordinal, so <c>"Simple"</c> is unrecognised and <c>"  simple  "</c> is not. The cost of the null fallback is
    ///     that a hand-edited junk value makes the node look like one that never answered and the boot backfill may then stamp
    ///     <c>advanced</c> over it — the same value the reader would have used anyway.
    /// </remarks>
    private static string? NormalizeUiMode(string? value)
    {
        var trimmed = TrimToNull(value);
        return StoredNodeSettings.IsValidUiMode(trimmed) ? trimmed : null;
    }

    /// <summary>
    ///     Unlike the external-access profile, an unrecognised value falls back to <see langword="null" /> rather than
    ///     to an "ask again" literal.
    /// </summary>
    /// <remarks>
    ///     Answering for the operator costs nothing here — the reader re-seeds the channel baked into the artifact,
    ///     which is the update visibility this build has always had, so a junk value simply makes the node look like
    ///     one that never chose. The comparison is ordinal, so <c>"Development"</c> is unrecognised and
    ///     <c>"  development  "</c> is not.
    /// </remarks>
    private static string? NormalizeUpdateChannel(string? value)
    {
        var trimmed = TrimToNull(value);
        return StoredNodeSettings.IsValidUpdateChannel(trimmed) ? trimmed : null;
    }

    private static string? NormalizeRecommendedTag(string? value)
    {
        var trimmed = TrimToNull(value);
        return StoredNodeSettings.IsValidRecommendedLlamaCppTag(trimmed) ? trimmed : null;
    }

    private static string? NormalizeKvCacheType(string? value)
    {
        // An unknown type falls back to null so the accessor re-seeds it to the default. This normalizer is the layer that must never let
        // a bad value through: LlamaServerLaunchPolicyOptions.Validate rejects one at host build, so persisted junk would take the node down.
        return LlamaServerKvCacheTypes.TryNormalize(value, out var normalized) ? normalized : null;
    }

    private static string? NormalizeContainerRuntimeSelection(string? value)
    {
        // Lower-cased through the one parser rather than trimmed in place, so the persisted spelling is the spelling the API carries and the
        // engine parses. An unrecognised value falls back to null so the reader re-seeds the default: a hand-edited file must not be able to leave the runtime layer unresolvable.
        return ContainerRuntimeSelectionParser.TryParse(value, out var selection)
            ? ContainerRuntimeSelectionParser.Format(selection)
            : null;
    }

    private static string? NormalizeSpeculativeMode(string? value)
    {
        // An unknown/malformed mode falls back to null so the accessor re-seeds it to disabled (never persist junk that
        // would surface as an invalid --spec-type at launch). A valid mode is kept trimmed.
        var trimmed = TrimToNull(value);
        return StoredNodeSettings.IsValidSpeculativeMode(trimmed) ? trimmed : null;
    }

    private static string? NormalizeAbsoluteUrl(string? value)
    {
        var trimmed = TrimToNull(value);
        if (trimmed is null)
        {
            return null;
        }

        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? trimmed
            : null;
    }

    private static IReadOnlyList<string>? NormalizeStringList(IReadOnlyList<string>? values)
    {
        if (values is null)
        {
            return null;
        }

        var cleaned = values
                      .Where(static value => !string.IsNullOrWhiteSpace(value))
                      .Select(static value => value.Trim())
                      .ToList();

        return cleaned.Count == 0 ? null : cleaned;
    }
}
