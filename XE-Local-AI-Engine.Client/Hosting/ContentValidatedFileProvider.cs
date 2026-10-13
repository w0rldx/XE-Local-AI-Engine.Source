namespace XE_Local_AI_Engine.Client.Hosting;

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

/// <summary>
///     Web-root provider whose non-hashed files report a Last-Modified derived from their SHA-256, so the StaticFiles
///     ETag (last-modified XOR length) changes whenever the bytes do.
/// </summary>
/// <remarks>
///     Release ZIPs stamp every file with the 1980 DOS epoch and the shell keeps its length across builds, so the real
///     ETag collided and an update was answered with 304. The middleware decides 304 before any response callback runs,
///     so the provider is the only seam. The synthetic date is never sent: <see cref="SpaStaticFileOptions" /> strips it.
///     Vite's content-hashed <c>/assets</c> files pass through unchanged.
/// </remarks>
internal sealed class ContentValidatedFileProvider : IFileProvider
{
    private readonly IFileProvider _inner;
    private readonly ConcurrentDictionary<string, ContentStamp> _stamps = new(StringComparer.OrdinalIgnoreCase);

    public ContentValidatedFileProvider(IFileProvider inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    public IFileInfo GetFileInfo(string subpath)
    {
        var file = _inner.GetFileInfo(subpath);
        if (!file.Exists || file.IsDirectory || IsHashedAsset(subpath))
        {
            return file;
        }

        var stamp = _stamps.GetValueOrDefault(subpath);
        if (stamp is null || stamp.Length != file.Length || stamp.RealLastModified != file.LastModified)
        {
            try
            {
                stamp = new ContentStamp { Length = file.Length, RealLastModified = file.LastModified, ContentLastModified = HashToDate(file) };
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Deleted or locked between the existence check and the read: serve it unstamped instead of failing with a 500.
                return file;
            }

            _stamps[subpath] = stamp;
        }

        return new ContentStampedFileInfo(file, stamp.ContentLastModified);
    }

    /// <summary>A Vite content-hashed file: under <c>/assets</c>, with an extension, not HTML. Any other path may be the shell.</summary>
    internal static bool IsHashedAsset(string? path)
    {
        var relative = path.AsSpan().TrimStart('/');
        return relative.StartsWith("assets/", StringComparison.OrdinalIgnoreCase)
               && Path.HasExtension(relative)
               && !relative.EndsWith(".html", StringComparison.OrdinalIgnoreCase);
    }

    public IDirectoryContents GetDirectoryContents(string subpath) => _inner.GetDirectoryContents(subpath);

    public IChangeToken Watch(string filter) => _inner.Watch(filter);

    /// <summary>Folds the content hash into whole seconds after the Unix epoch (1970 to 2038), which the middleware truncates to anyway.</summary>
    private static DateTimeOffset HashToDate(IFileInfo file)
    {
#pragma warning disable MA0045 // IFileProvider.GetFileInfo is synchronous; the hash runs once per file version, then comes from the cache.
        using var stream = file.CreateReadStream();
        var seconds = BinaryPrimitives.ReadUInt32BigEndian(SHA256.HashData(stream)) & int.MaxValue;
#pragma warning restore MA0045
        return DateTimeOffset.UnixEpoch.AddSeconds(seconds);
    }

    private sealed class ContentStamp
    {
        public required long Length { get; init; }
        public required DateTimeOffset RealLastModified { get; init; }
        public required DateTimeOffset ContentLastModified { get; init; }
    }

    private sealed class ContentStampedFileInfo : IFileInfo
    {
        private readonly IFileInfo _inner;

        public ContentStampedFileInfo(IFileInfo inner, DateTimeOffset lastModified)
        {
            _inner = inner;
            LastModified = lastModified;
        }

        public bool Exists => _inner.Exists;
        public long Length => _inner.Length;
        public string? PhysicalPath => _inner.PhysicalPath;
        public string Name => _inner.Name;
        public DateTimeOffset LastModified { get; }
        public bool IsDirectory => _inner.IsDirectory;
        public Stream CreateReadStream() => _inner.CreateReadStream();
    }
}
