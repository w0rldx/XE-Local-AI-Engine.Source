namespace XE_Local_AI_Engine.Client.Services.Transcription;

using System.Buffers;

/// <summary>
///     The engine-owned temporary files one upload occupies, and the single thing that deletes them.
/// </summary>
/// <remarks>
///     One owner, on purpose: the slot is created before the request body is read and disposed after the transcription
///     finishes, so every way an upload can end — an overrun, a client disconnect, a mid-copy <see cref="IOException" />, a
///     cancellation, a failed transcode, a clean success — leaves through the same <see cref="DisposeAsync" />, and a second
///     owner could only produce a double delete or a leak. The transcode destination is registered through
///     <see cref="AddOwnedPath" /> BEFORE the converter starts, because a converter killed halfway leaves a partial audio file.
/// </remarks>
public sealed class TranscriptionUploadSlot : IAsyncDisposable
{
    private const int CopyBufferBytes = 64 * 1024;

    private readonly List<string> _ownedPaths;
    private readonly string _directory;
    private readonly ILogger _logger;
    private bool _disposed;

    internal TranscriptionUploadSlot(Guid sessionId, string directory, string extension, ILogger logger)
    {
        SessionId = sessionId;
        _directory = directory;
        _logger = logger;
        SourcePath = Path.Combine(directory, string.Concat(Guid.NewGuid().ToString("N"), extension));
        _ownedPaths = [SourcePath];
    }

    /// <summary>The session this upload belongs to.</summary>
    public Guid SessionId { get; }

    /// <summary>The server-named file the request body is streamed into. Never built from a client string.</summary>
    public string SourcePath { get; }

    /// <summary>Mints and tracks a second owned file in the same directory, and returns its path.</summary>
    public string AddOwnedPath(string extension)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var path = Path.Combine(_directory, string.Concat(Guid.NewGuid().ToString("N"), extension));
        _ownedPaths.Add(path);
        return path;
    }

    /// <summary>
    ///     Streams <paramref name="source" /> into <see cref="SourcePath" />, refusing to write more than
    ///     <paramref name="maxBytes" />.
    /// </summary>
    /// <exception cref="TranscriptionUploadTooLargeException">The body exceeded <paramref name="maxBytes" />.</exception>
    public async Task<long> CopyFromAsync(Stream source, long maxBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferBytes);
        try
        {
            var destination = new FileStream(SourcePath, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferBytes, useAsync: true);
            await using (destination)
            {
                long written = 0;
                while (true)
                {
                    var read = await source.ReadAsync(buffer.AsMemory(0, CopyBufferBytes), cancellationToken);
                    if (read == 0)
                    {
                        return written;
                    }

                    written += read;
                    if (written > maxBytes)
                    {
                        // Stop reading here rather than draining the body: the bytes already written are deleted by
                        // DisposeAsync, and the caller answers before the client can send any more of them.
                        throw new TranscriptionUploadTooLargeException(TranscriptionUploadTooLargeException.MessageFor(maxBytes));
                    }

                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Deletes every owned file. Idempotent, and never throws.</summary>
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        foreach (var path in _ownedPaths)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A temporary file that could not be removed must never turn a finished transcription into a failed request.
                // Only the leaf name is logged: the path is engine-generated, but the log is not the place to correlate it.
                _logger.LogWarning(exception,
                    "Could not delete the temporary transcription file {FileName}.",
                    Path.GetFileName(path));
            }
        }

        return ValueTask.CompletedTask;
    }
}
