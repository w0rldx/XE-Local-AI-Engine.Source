namespace XE_Local_AI_Engine.Client.Services.NodeSettings.Implementation;

using System.Text.Json;
using XE_Local_AI_Engine.Providers.Abstractions;

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
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return new StoredNodeSettings();
            }

            try
            {
                await using var fileStream = File.OpenRead(_settingsPath);
                var settings = await JsonSerializer.DeserializeAsync<StoredNodeSettings>(fileStream, SerializerOptions, cancellationToken).ConfigureAwait(false);
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

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var fileStream = File.Create(_settingsPath))
            {
                await JsonSerializer.SerializeAsync(fileStream, normalizedSettings, SerializerOptions, cancellationToken).ConfigureAwait(false);
            }

            ApplyOwnerOnlyPermissions();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    ///     On non-Windows restrict the settings file to owner read/write (0600), matching the key-file posture. Windows
    ///     relies on the per-user data-directory ACL instead — <see cref="File.SetUnixFileMode" /> is unsupported there.
    /// </summary>
    private void ApplyOwnerOnlyPermissions()
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_settingsPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static StoredNodeSettings Normalize(StoredNodeSettings settings)
    {
        if (settings.MaxMessageRequestTimeoutSeconds is < StoredNodeSettings.MinMaxMessageRequestTimeoutSeconds or > StoredNodeSettings.MaxMaxMessageRequestTimeoutSeconds)
        {
            return new StoredNodeSettings();
        }

        var defaultModelName = string.IsNullOrWhiteSpace(settings.DefaultModelName)
            ? null
            : settings.DefaultModelName.Trim();

        return settings with
        {
            DefaultModelName = defaultModelName
        };
    }
}
