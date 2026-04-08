namespace XE_Local_AI_Engine.Services.DeadLetter;

using System.Text.Json;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Configuration;
using XE_Local_AI_Engine.Models;

public sealed class FileDeadLetterStore : IDeadLetterStore, IDisposable
{
    private const long MaxQueueSizeBytes = 100L * 1024 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger<FileDeadLetterStore> _logger;
    private readonly string _queueDirectoryPath;

    public FileDeadLetterStore(IOptions<WorkerNodeOptions> workerNodeOptions,
        ILogger<FileDeadLetterStore> logger)
    {
        ArgumentNullException.ThrowIfNull(workerNodeOptions);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var configuredPath = workerNodeOptions.Value.DeadLetterQueuePath;
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            throw new InvalidOperationException("WorkerNode:DeadLetterQueuePath must be configured.");
        }

        _queueDirectoryPath = ResolveQueueDirectoryPath(configuredPath);
        Directory.CreateDirectory(_queueDirectoryPath);
    }

    public async Task EnqueueAsync(InvocationFailedPayload payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            Directory.CreateDirectory(_queueDirectoryPath);

            var entryPath = Path.Combine(_queueDirectoryPath, BuildFileName(payload.InvocationId));
            await using var stream = File.Create(entryPath);
            await JsonSerializer.SerializeAsync(stream, payload, SerializerOptions, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

            await EnforceSizeLimitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<InvocationFailedPayload>> GetPendingAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var pending = new List<InvocationFailedPayload>();

            foreach (var filePath in EnumerateEntryPaths())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var payload = await ReadPayloadAsync(filePath, cancellationToken).ConfigureAwait(false);
                if (payload is not null)
                {
                    pending.Add(payload);
                }
            }

            return pending;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync(Guid invocationId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            foreach (var filePath in EnumerateEntryPaths())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var payload = await ReadPayloadAsync(filePath, cancellationToken).ConfigureAwait(false);
                if (payload?.InvocationId != invocationId)
                {
                    continue;
                }

                File.Delete(filePath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public long GetCurrentSizeBytes()
    {
        return EnumerateEntryPaths()
               .Select(static path => new FileInfo(path))
               .Where(static fileInfo => fileInfo.Exists)
               .Sum(static fileInfo => fileInfo.Length);
    }

    public void Dispose()
    {
        _gate.Dispose();
    }

    private async Task EnforceSizeLimitAsync(CancellationToken cancellationToken)
    {
        var currentSizeBytes = GetCurrentSizeBytes();
        if (currentSizeBytes <= MaxQueueSizeBytes)
        {
            return;
        }

        foreach (var filePath in EnumerateEntryPaths())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fileInfo = new FileInfo(filePath);
            if (!fileInfo.Exists)
            {
                continue;
            }

            _logger.LogWarning("Dead letter queue exceeded {MaxQueueSizeBytes} bytes. Evicting oldest entry {FileName}.",
                MaxQueueSizeBytes,
                fileInfo.Name);

            var fileLength = fileInfo.Length;
            fileInfo.Delete();
            currentSizeBytes -= fileLength;

            if (currentSizeBytes <= MaxQueueSizeBytes)
            {
                break;
            }
        }
    }

    private IEnumerable<string> EnumerateEntryPaths()
    {
        if (!Directory.Exists(_queueDirectoryPath))
        {
            return Array.Empty<string>();
        }

        return Directory.EnumerateFiles(_queueDirectoryPath, "*.json", SearchOption.TopDirectoryOnly)
                        .OrderBy(static path => path, StringComparer.Ordinal);
    }

    private async Task<InvocationFailedPayload?> ReadPayloadAsync(string filePath, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(filePath);
            return await JsonSerializer.DeserializeAsync<InvocationFailedPayload>(stream, SerializerOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "Dead letter queue entry {FilePath} is invalid JSON and will be skipped.", filePath);
            return null;
        }
        catch (IOException exception)
        {
            _logger.LogWarning(exception, "Dead letter queue entry {FilePath} could not be read and will be skipped.", filePath);
            return null;
        }
    }

    private static string BuildFileName(Guid invocationId)
    {
        return $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{invocationId:N}.json";
    }

    private static string ResolveQueueDirectoryPath(string configuredPath)
    {
        var baseDirectory = AppContext.BaseDirectory;
        var candidatePath = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(baseDirectory, configuredPath);

        return Path.GetFullPath(candidatePath);
    }
}
