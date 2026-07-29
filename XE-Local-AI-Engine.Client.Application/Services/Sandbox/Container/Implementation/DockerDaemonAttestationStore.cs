namespace XE_Local_AI_Engine.Client.Services.Sandbox.Container.Implementation;

using System.Text.Json;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     File-backed <see cref="IDockerDaemonAttestationStore" />, writing a single JSON document under the node data
///     directory. Writes go through a temporary file and an atomic move so a crash mid-write leaves the previous
///     approval intact rather than a truncated one — a corrupt attestation would present to the operator as "your
///     daemon changed", which is the one message that must never be spurious.
/// </summary>
internal sealed class DockerDaemonAttestationStore : IDockerDaemonAttestationStore, IDisposable
{
    internal const string DirectoryName = "development";
    internal const string FileName = "docker-daemon-attestation.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _filePath;
    private readonly ILogger<DockerDaemonAttestationStore> _logger;
    private readonly SemaphoreSlim _sync = new(1, 1);

    public DockerDaemonAttestationStore(INodeDataDirectory nodeDataDirectory, ILogger<DockerDaemonAttestationStore> logger)
    {
        ArgumentNullException.ThrowIfNull(nodeDataDirectory);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _filePath = Path.Combine(nodeDataDirectory.Root, DirectoryName, FileName);
    }

    public void Dispose()
    {
        _sync.Dispose();
    }

    public async Task<DockerDaemonAttestation?> ReadAsync(CancellationToken cancellationToken = default)
    {
        await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_filePath))
            {
                return null;
            }

            await using var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return await JsonSerializer.DeserializeAsync<DockerDaemonAttestation>(stream, SerializerOptions, cancellationToken)
                                       .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            // Treat an unreadable record as "never approved" rather than as "changed". The operator is then asked to
            // approve the daemon they are actually on, which is a recoverable prompt; reporting a change they did not
            // make would teach them to click through the one warning that is supposed to stop them.
            _logger.LogWarning(exception,
                "The Docker daemon attestation at {AttestationPath} could not be read and is being treated as absent.",
                _filePath);
            return null;
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task WriteAsync(DockerDaemonAttestation attestation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attestation);

        await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_filePath)!;
            Directory.CreateDirectory(directory);

            var temporaryPath = _filePath + ".tmp";
            await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, attestation, SerializerOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        finally
        {
            _sync.Release();
        }
    }
}
