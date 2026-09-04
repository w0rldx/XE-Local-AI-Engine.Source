namespace XE_Local_AI_Engine.Client.Services.ExternalProviders.Implementation;

using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.External;
using XE_Local_AI_Engine.Providers.OpenAICompatible.Core;

/// <summary>
///     Persistence boundary for the operator's external OpenAI-compatible connections: one data-protected JSON file
///     next to the node's other secrets, written 0600, guarded by a process-wide lock and a compare-and-swap revision.
/// </summary>
/// <remarks>
///     <para>
///         Modelled on <c>CloudCredentialStore</c> — same protector-per-purpose, same "decryption failed ⇒ quarantine
///         and report empty" posture, same create-at-0600 write — because both files hold API keys and a second,
///         subtly different secret-file discipline is how one of them ends up world-readable.
///     </para>
///     <para>
///         The base URL is normalized HERE and nowhere else. The outbound guard pins every request to the stored value,
///         so a descriptor carrying an un-normalized address would widen the guard to whatever the operator typed. The
///         same reasoning applies to the connection slug: it is canonicalized once at write time, which is what keeps
///         the case-INSENSITIVE provider map and the ORDINAL tool-capable allow-list agreeing about one model.
///     </para>
/// </remarks>
public sealed class ExternalProviderStore : IExternalProviderStore, IDisposable
{
    private const string StoreFileName = "external-providers.enc";
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly SemaphoreSlim _lock = new(initialCount: 1, maxCount: 1);
    private readonly ILogger<ExternalProviderStore> _logger;
    private readonly IDataProtector _protector;
    private readonly string _storePath;

    public ExternalProviderStore(IDataProtectionProvider dataProtectionProvider,
        INodeDataDirectory dataDirectory,
        ILogger<ExternalProviderStore> logger)
    {
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);
        ArgumentNullException.ThrowIfNull(dataDirectory);

        _protector = dataProtectionProvider.CreateProtector("WorkerNode.ExternalProviderStore.v1");
        _storePath = Path.Combine(dataDirectory.Root, StoreFileName);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<StoredExternalProviderConfig> LoadAsync(CancellationToken cancellationToken = default)
    {
        return await ReadForWriteAsync(cancellationToken).ConfigureAwait(false) is ExternalProviderLoadResult.Loaded loaded
            ? loaded.Config
            : new StoredExternalProviderConfig();
    }

    /// <inheritdoc />
    public async Task<ExternalProviderLoadResult> ReadForWriteAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadUnlockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<ExternalProviderWriteResult> SaveConnectionAsync(ExternalProviderConnectionSaveRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Validate BEFORE taking the lock: a rejected request must not serialize behind an in-flight write, and the
        // normalized values it produces are what actually get stored.
        var candidate = Validate(request);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await LoadForWriteUnlockedAsync(cancellationToken).ConfigureAwait(false);
            if (IsSuperseded(current, request.ExpectedRevision))
            {
                return new ExternalProviderWriteResult.Superseded(current);
            }

            var existing = FindConnection(current, candidate.Id);
            var merged = candidate with
            {
                ApiKey = MergeApiKey(request, existing, candidate.BaseUrl)
            };

            if (existing is not null && IsUnchanged(existing, merged))
            {
                // Identical save: skip the write so an idempotent reconciliation pass does not churn the file (and the
                // revision every open editor is holding) for nothing.
                return new ExternalProviderWriteResult.Committed(current, Changed: false);
            }

            var connections = current.Connections.ToList();
            var index = connections.FindIndex(connection => IsSameId(connection.Id, merged.Id));
            if (index < 0)
            {
                if (connections.Count >= ExternalProviderStoreSchema.MaxConnections)
                {
                    throw new ExternalProviderValidationException($"At most {ExternalProviderStoreSchema.MaxConnections} external connections can be configured.");
                }

                connections.Add(merged);
            }
            else
            {
                // Replaced in place so editing a connection never reorders the operator's list.
                connections[index] = merged;
            }

            return new ExternalProviderWriteResult.Committed(await WriteAsync(connections, cancellationToken).ConfigureAwait(false), Changed: true);
        }
        finally
        {
            _ = _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<ExternalProviderWriteResult> DeleteConnectionAsync(string connectionId,
        string? expectedRevision,
        CancellationToken cancellationToken = default)
    {
        var canonicalId = CanonicalizeId(connectionId);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await LoadForWriteUnlockedAsync(cancellationToken).ConfigureAwait(false);
            if (IsSuperseded(current, expectedRevision))
            {
                return new ExternalProviderWriteResult.Superseded(current);
            }

            var remaining = current.Connections.Where(connection => !IsSameId(connection.Id, canonicalId)).ToList();
            if (remaining.Count == current.Connections.Count)
            {
                // Already gone. A retried delete after a partial failure is the reconciliation path's normal shape, so
                // it reports success-with-no-change rather than an error the caller would have to special-case.
                return new ExternalProviderWriteResult.Committed(current, Changed: false);
            }

            return new ExternalProviderWriteResult.Committed(await WriteAsync(remaining, cancellationToken).ConfigureAwait(false), Changed: true);
        }
        finally
        {
            _ = _lock.Release();
        }
    }

    public void Dispose()
    {
        _lock.Dispose();
    }

    /// <summary>
    ///     Projects one stored connection onto the key-free descriptor every catalog, UI and policy consumer sees.
    ///     Shared with the registry so the two can never disagree about what a stored row means.
    /// </summary>
    internal static ExternalProviderConnectionDescriptor ToDescriptor(StoredExternalProviderConnection connection)
    {
        return new ExternalProviderConnectionDescriptor
        {
            Id = connection.Id,
            DisplayName = connection.DisplayName,
            // Already normalized at save; parsed, never re-normalized, so a stored value that somehow drifted is
            // visible as a load failure rather than silently repaired into a base the guard never reviewed.
            BaseUrl = new Uri(connection.BaseUrl, UriKind.Absolute),
            Locality = connection.Locality,
            Timeout = connection.TimeoutSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null
        };
    }

    /// <summary>Projects one stored model onto its declaration read model.</summary>
    internal static ExternalProviderModelDescriptor ToDescriptor(StoredExternalProviderModel model)
    {
        return new ExternalProviderModelDescriptor
        {
            WireId = model.WireId,
            DisplayName = model.DisplayName,
            ContextLength = model.ContextLength,
            SupportsTools = model.SupportsTools,
            SupportsVision = model.SupportsVision,
            SupportsReasoning = model.SupportsReasoning,
            SupportsReasoningEffort = model.SupportsReasoningEffort,
            DefaultReasoningEffort = model.DefaultReasoningEffort
        };
    }

    /// <summary>
    ///     Resolves the key to store: an explicit clear wins, then a supplied key, then whatever is already stored —
    ///     the last of which is allowed ONLY while the connection stays on the same origin.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The carry-forward case is why this method exists. The editor masks the key and sends nothing back, so
    ///         treating a blank key as "clear it" would silently de-authenticate a working connection the first time an
    ///         operator renamed it — a chat failure, later, with no visible cause.
    ///     </para>
    ///     <para>
    ///         But a key is a credential for ONE origin, and carrying it forward across an origin change is how an
    ///         operator-API caller who cannot read the encrypted key extracts it anyway: point the connection at a
    ///         listener they control, save without a key, and the node presents the stored secret as a bearer token on
    ///         the next request. So an origin change requires the key to be re-entered or explicitly cleared — an
    ///         explicit decision by whoever is moving the endpoint, which is exactly the decision being stolen here.
    ///         A path change on the same origin is not a re-authorization: the credential's audience has not moved.
    ///     </para>
    /// </remarks>
    private static string? MergeApiKey(ExternalProviderConnectionSaveRequest request,
        StoredExternalProviderConnection? existing,
        string normalizedBaseUrl)
    {
        if (request.ClearApiKey)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(request.ApiKey))
        {
            return request.ApiKey.Trim();
        }

        if (string.IsNullOrWhiteSpace(existing?.ApiKey))
        {
            return null;
        }

        if (!IsSameOrigin(existing.BaseUrl, normalizedBaseUrl))
        {
            throw new ExternalProviderValidationException(
                "This connection's endpoint moved to a different host, so its stored API key was not carried over. Enter the key again for the new endpoint, or clear it to connect without one.");
        }

        return existing.ApiKey;
    }

    /// <summary>
    ///     Whether two NORMALIZED base URLs address the same origin — scheme, host and port. The unit of credential
    ///     trust, and compared here rather than by string equality so a path-only edit (<c>/v1</c> to <c>/openai/v1</c>)
    ///     does not force a needless key re-entry.
    /// </summary>
    internal static bool IsSameOrigin(string? left, string? right)
    {
        return Uri.TryCreate(left, UriKind.Absolute, out var leftUri)
               && Uri.TryCreate(right, UriKind.Absolute, out var rightUri)
               && string.Equals(leftUri.GetLeftPart(UriPartial.Authority),
                   rightUri.GetLeftPart(UriPartial.Authority),
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSameId(string left, string right)
    {
        return string.Equals(left, right, StringComparison.Ordinal);
    }

    private static StoredExternalProviderConnection? FindConnection(StoredExternalProviderConfig config, string canonicalId)
    {
        return config.Connections.FirstOrDefault(connection => IsSameId(connection.Id, canonicalId));
    }

    private static bool IsSuperseded(StoredExternalProviderConfig current, string? expectedRevision)
    {
        return expectedRevision is not null && !string.Equals(current.Revision, expectedRevision, StringComparison.Ordinal);
    }

    private static string CanonicalizeId(string? connectionId)
    {
        // Canonicalized through the id type rather than a local lowering pass: that type is the single definition of
        // the slug's canonical spelling, and the map/allow-list agreement depends on there being exactly one.
        var canonical = ExternalModelId.CanonicalizeConnectionId(connectionId);
        if (!ExternalModelId.IsValidConnectionId(canonical))
        {
            throw new ExternalProviderValidationException($"An external connection id must match {ExternalModelId.ConnectionIdPattern}.");
        }

        return canonical;
    }

    /// <summary>
    ///     Structural equality of two connections, models included. Record equality would compare the model LISTS by
    ///     reference, so it reports every save as a change and the no-op skip below would never fire.
    /// </summary>
    private static bool IsUnchanged(StoredExternalProviderConnection left, StoredExternalProviderConnection right)
    {
        var noModels = Array.Empty<StoredExternalProviderModel>();
        return left with
               {
                   Models = noModels
               } == right with
               {
                   Models = noModels
               }
               && left.Models.SequenceEqual(right.Models);
    }

    private static StoredExternalProviderConnection Validate(ExternalProviderConnectionSaveRequest request)
    {
        var id = CanonicalizeId(request.Id);

        var displayName = request.DisplayName?.Trim() ?? string.Empty;
        if (displayName.Length == 0 || displayName.Length > ExternalProviderStoreSchema.MaxDisplayNameLength)
        {
            throw new ExternalProviderValidationException($"An external connection display name must be 1-{ExternalProviderStoreSchema.MaxDisplayNameLength} characters.");
        }

        if (!OpenAICompatibleBaseAddress.TryNormalize(request.BaseUrl, out var baseUrl))
        {
            throw new ExternalProviderValidationException("An external connection base URL must be an absolute http(s) address without credentials, query, or fragment.");
        }

        if (!Enum.IsDefined(request.Locality))
        {
            throw new ExternalProviderValidationException("An external connection declares an unsupported locality.");
        }

        if (request.TimeoutSeconds is { } timeout
            && timeout is < ExternalProviderStoreSchema.MinTimeoutSeconds or > ExternalProviderStoreSchema.MaxTimeoutSeconds)
        {
            throw new ExternalProviderValidationException(
                $"An external connection timeout must be {ExternalProviderStoreSchema.MinTimeoutSeconds}-{ExternalProviderStoreSchema.MaxTimeoutSeconds} seconds.");
        }

        return new StoredExternalProviderConnection
        {
            Id = id,
            DisplayName = displayName,
            BaseUrl = baseUrl.AbsoluteUri,
            Locality = request.Locality,
            TimeoutSeconds = request.TimeoutSeconds,
            Models = ValidateModels(request.Models)
        };
    }

    private static IReadOnlyList<StoredExternalProviderModel> ValidateModels(IReadOnlyList<ExternalProviderModelSaveRequest> models)
    {
        ArgumentNullException.ThrowIfNull(models);
        if (models.Count > ExternalProviderStoreSchema.MaxModelsPerConnection)
        {
            throw new ExternalProviderValidationException($"At most {ExternalProviderStoreSchema.MaxModelsPerConnection} models can be registered on one external connection.");
        }

        // Ordinal, because remote model ids ARE case-sensitive: "Qwen/qwen3" and "qwen/Qwen3" are two ids on the wire,
        // and collapsing them here would make one of them unreachable.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var validated = new List<StoredExternalProviderModel>(models.Count);
        foreach (var model in models)
        {
            var wireId = model.WireId?.Trim() ?? string.Empty;
            if (!ExternalModelId.IsValidWireId(wireId))
            {
                throw new ExternalProviderValidationException($"An external model id must be 1-{ExternalModelId.MaxWireIdLength} characters of [A-Za-z0-9._:/-] with no traversal or edge slash.");
            }

            if (!seen.Add(wireId))
            {
                throw new ExternalProviderValidationException($"The external model '{wireId}' is registered more than once on this connection.");
            }

            if (model.ContextLength is <= 0)
            {
                throw new ExternalProviderValidationException("A declared external model context length must be a positive number of tokens.");
            }

            if (!ReasoningEffortNormalizer.IsValid(model.DefaultReasoningEffort))
            {
                throw new ExternalProviderValidationException($"'{model.DefaultReasoningEffort}' is not a recognized reasoning effort.");
            }

            // Refused even though the vocabulary accepts it: `auto` is resolved PER TURN by this node's dispatcher into
            // a concrete tier, and the dispatcher's FAST tier is a node-local model swap this node cannot perform for a
            // remote endpoint. A registered model's DEFAULT effort is a wire value, so `auto` would either leak to the
            // endpoint or be silently dropped — neither is a defensible reading of the operator's input.
            if (string.Equals(ReasoningEffortNormalizer.Normalize(model.DefaultReasoningEffort), "auto", StringComparison.Ordinal))
            {
                throw new ExternalProviderValidationException(
                    "A registered model's default reasoning effort cannot be 'auto'; auto is resolved per turn by this node.");
            }

            // Refused, not silently canonicalized. Every capability here is an operator ASSERTION about a remote server
            // no probe can interrogate, and "it does not reason, but here is its default reasoning effort" is not a
            // claim with a defensible reading — accepting it would put reasoning_effort on the wire for a model the
            // catalog simultaneously reports as non-reasoning, and quietly dropping it would hide a form the operator
            // filled in wrong. The effort switch is only meaningful alongside a reasoning channel.
            if (!model.SupportsReasoning && (model.SupportsReasoningEffort || !string.IsNullOrWhiteSpace(model.DefaultReasoningEffort)))
            {
                throw new ExternalProviderValidationException($"The external model '{wireId}' declares a reasoning effort but not reasoning support. Enable reasoning, or remove the effort settings.");
            }

            if (!model.SupportsReasoningEffort && !string.IsNullOrWhiteSpace(model.DefaultReasoningEffort))
            {
                throw new ExternalProviderValidationException(
                    $"The external model '{wireId}' declares a default reasoning effort but not graded effort support. Enable effort support, or remove the default.");
            }

            var displayName = model.DisplayName?.Trim();
            if (displayName is { Length: > ExternalProviderStoreSchema.MaxDisplayNameLength })
            {
                throw new ExternalProviderValidationException($"An external model display name must be at most {ExternalProviderStoreSchema.MaxDisplayNameLength} characters.");
            }

            validated.Add(new StoredExternalProviderModel
            {
                WireId = wireId,
                DisplayName = string.IsNullOrEmpty(displayName) ? null : displayName,
                ContextLength = model.ContextLength,
                SupportsTools = model.SupportsTools,
                SupportsVision = model.SupportsVision,
                SupportsReasoning = model.SupportsReasoning,
                SupportsReasoningEffort = model.SupportsReasoningEffort,
                DefaultReasoningEffort = ReasoningEffortNormalizer.Normalize(model.DefaultReasoningEffort)
            });
        }

        return validated;
    }

    private async Task<ExternalProviderLoadResult> LoadUnlockedAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_storePath))
        {
            return new ExternalProviderLoadResult.Missing();
        }

        byte[] payload;
        try
        {
            var protectedPayload = await File.ReadAllBytesAsync(_storePath, cancellationToken).ConfigureAwait(false);
            payload = _protector.Unprotect(protectedPayload);
        }
        catch (CryptographicException exception)
        {
            // The key ring rotated out from under the file (a node re-key, a restored profile). The payload can never
            // be recovered, and leaving it in place would make every subsequent save fail its read-modify-write, so it
            // is quarantined exactly as the cloud credential store quarantines its own. Quarantined means GONE, which
            // is why this reports Missing rather than Unreadable: after the delete there genuinely is no config.
            _logger.LogWarning(exception, "External provider store decryption failed. Clearing the stored external connections.");
            ClearStoreFileBestEffort();
            return new ExternalProviderLoadResult.Missing();
        }
        catch (IOException exception)
        {
            // Transient, and NOT recoverable information: the file is probably fine and a concurrent reader or AV
            // scanner is holding it. Reported as unreadable so a writer refuses rather than reconciling the operator's
            // configuration away on the strength of a locked handle.
            _logger.LogWarning(exception, "External provider store could not be read from disk.");
            return new ExternalProviderLoadResult.Unreadable("The external provider store could not be read from disk.");
        }

        try
        {
            var config = JsonSerializer.Deserialize<StoredExternalProviderConfig>(payload, SerializerOptions)
                         ?? throw new JsonException("The stored external provider config deserialized to null.");
            if (config.SchemaVersion > ExternalProviderStoreSchema.CurrentVersion)
            {
                // Written by a NEWER build. Refuse to interpret it, and refuse to delete it: the operator downgraded,
                // and silently discarding their connections (and keys) would be the worse of the two failures.
                _logger.LogWarning(
                    "The external provider store was written at schema version {StoredVersion}, newer than this build's {CurrentVersion}; leaving it untouched and refusing to write it.",
                    config.SchemaVersion,
                    ExternalProviderStoreSchema.CurrentVersion);
                return new ExternalProviderLoadResult.UnsupportedSchema(config.SchemaVersion);
            }

            return new ExternalProviderLoadResult.Loaded(config);
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "External provider store could not be deserialized. Clearing the stored external connections.");
            ClearStoreFileBestEffort();
            return new ExternalProviderLoadResult.Missing();
        }
    }

    /// <summary>
    ///     The current configuration for a WRITE, or the typed refusal that says why one must not happen.
    /// </summary>
    private async Task<StoredExternalProviderConfig> LoadForWriteUnlockedAsync(CancellationToken cancellationToken)
    {
        return await LoadUnlockedAsync(cancellationToken).ConfigureAwait(false) switch
        {
            ExternalProviderLoadResult.Loaded loaded => loaded.Config,
            ExternalProviderLoadResult.Missing => new StoredExternalProviderConfig(),
            ExternalProviderLoadResult.Unreadable unreadable => throw new ExternalProviderValidationException($"{unreadable.Reason} Nothing was changed; retry once the file is accessible."),
            ExternalProviderLoadResult.UnsupportedSchema unsupported => throw new ExternalProviderValidationException(
                $"The external connection store was written by a newer version of this application (schema {unsupported.StoredVersion}). Nothing was changed. Upgrade, or remove the store file to start over."),
            _ => throw new ExternalProviderValidationException("The external connection store is in an unrecognized state; nothing was changed.")
        };
    }

    private async Task<StoredExternalProviderConfig> WriteAsync(IReadOnlyList<StoredExternalProviderConnection> connections,
        CancellationToken cancellationToken)
    {
        var config = new StoredExternalProviderConfig
        {
            SchemaVersion = ExternalProviderStoreSchema.CurrentVersion,
            // A fresh opaque value per write, not a counter: nothing may infer edit ORDER from a revision, and a
            // counter restored from a backup would collide with an edit made after it.
            Revision = Guid.NewGuid().ToString("N"),
            Connections = connections
        };

        var protectedPayload = _protector.Protect(JsonSerializer.SerializeToUtf8Bytes(config, SerializerOptions));
        await WriteProtectedPayloadAsync(protectedPayload, cancellationToken).ConfigureAwait(false);
        SecureFilePermissions.Apply(_storePath);
        return config;
    }

    /// <summary>
    ///     Writes the protected blob, creating the file 0600 on *nix in the same syscall that creates it. See
    ///     <c>CloudCredentialStore.WriteProtectedPayloadAsync</c> for the umask window this closes and why the
    ///     narrowing pass still runs afterwards.
    /// </summary>
    private async Task WriteProtectedPayloadAsync(byte[] protectedPayload, CancellationToken cancellationToken)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous
        };

        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        var stream = new FileStream(_storePath, options);
        await using (stream.ConfigureAwait(false))
        {
            await stream.WriteAsync(protectedPayload, cancellationToken).ConfigureAwait(false);
        }
    }

    private void ClearStoreFileBestEffort()
    {
        try
        {
            if (File.Exists(_storePath))
            {
                File.Delete(_storePath);
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to delete the external provider store file.");
        }
    }
}
