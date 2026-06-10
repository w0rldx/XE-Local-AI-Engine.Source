namespace XE_Local_AI_Engine.Installer.State;

using System.Text.Json;

/// <summary>
///     Disk-backed <see cref="IInstallStateStore" /> writing two JSON files under the installer state
///     directory (production: <c>%ProgramData%\XE-Local-AI-Engine\installer\</c>). The manifest is
///     written atomically (temp file + replace) because it is the last install step and its presence
///     is the "already installed" detector (plan §6.1 / invariant 4).
/// </summary>
public sealed class FileInstallStateStore : IInstallStateStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _manifestPath;
    private readonly string _statePath;

    public FileInstallStateStore(string stateDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        _manifestPath = Path.Combine(stateDirectory, "install-manifest.json");
        _statePath = Path.Combine(stateDirectory, "install-state.json");
    }

    public Task<InstallManifest?> ReadManifestAsync(CancellationToken cancellationToken = default) =>
        ReadAsync<InstallManifest>(_manifestPath, cancellationToken);

    public Task WriteManifestAsync(InstallManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return WriteAtomicAsync(_manifestPath, manifest, cancellationToken);
    }

    public Task DeleteManifestAsync(CancellationToken cancellationToken = default)
    {
        Delete(_manifestPath);
        return Task.CompletedTask;
    }

    public Task<InstallState?> ReadStateAsync(CancellationToken cancellationToken = default) =>
        ReadAsync<InstallState>(_statePath, cancellationToken);

    public Task WriteStateAsync(InstallState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        return WriteAtomicAsync(_statePath, state, cancellationToken);
    }

    public Task DeleteStateAsync(CancellationToken cancellationToken = default)
    {
        Delete(_statePath);
        return Task.CompletedTask;
    }

    private static async Task<T?> ReadAsync<T>(string path, CancellationToken cancellationToken) where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, SerializerOptions, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteAtomicAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = path + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, value, SerializerOptions, cancellationToken).ConfigureAwait(false);
        }

        File.Move(tempPath, path, overwrite: true);
    }

    private static void Delete(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
