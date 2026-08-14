namespace XE_Local_AI_Engine.Providers.HuggingFace.Implementation;

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>
///     Holds the exact validated source handle used for inspection and copying. Linux uses atomic O_NOFOLLOW plus
///     mount/inode identity; Windows uses handle identity. Other platforms retain the ancestor/reparse walk and stable
///     length/write-time checks as documented best effort.
/// </summary>
internal sealed class ValidatedGgufImportSource : IAsyncDisposable
{
    private const int LinuxReadOnlyNoFollowNonBlockingCloseOnExec = 0x0 | 0x800 | 0x20000 | 0x80000;
    private readonly string _canonicalPath;
    private readonly SourceIdentity _identity;
    private readonly DateTime _lastWriteUtc;
    private readonly FileStream _stream;

    private ValidatedGgufImportSource(string canonicalPath, FileStream stream, SourceIdentity identity, DateTime lastWriteUtc)
    {
        _canonicalPath = canonicalPath;
        _stream = stream;
        _identity = identity;
        _lastWriteUtc = lastWriteUtc;
    }

    public long Length => _stream.Length;

    public string DisplayName => Path.GetFileName(_canonicalPath);

    public Stream Stream => _stream;

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "The successful FileStream constructor takes ownership of the SafeFileHandle; every constructor failure disposes it below.")]
    public static ValidatedGgufImportSource Open(string sourcePath, string modelsDirectory)
    {
        try
        {
            if (!Path.IsPathFullyQualified(sourcePath)
                || !string.Equals(Path.GetExtension(sourcePath), ".gguf", StringComparison.OrdinalIgnoreCase))
            {
                throw new GgufImportException(GgufImportRejectionCode.InvalidSource, "The selected source must be an absolute GGUF file path.");
            }

            var canonicalPath = Path.GetFullPath(sourcePath);
            if (!File.Exists(canonicalPath))
            {
                throw new FileNotFoundException("The selected source does not exist.", canonicalPath);
            }

            EnsureNoReparseComponents(canonicalPath);
            var managedRoot = Path.GetFullPath(modelsDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (canonicalPath.StartsWith(managedRoot, comparison))
            {
                throw new GgufImportException(GgufImportRejectionCode.InvalidSource, "The selected source must be outside the managed models directory.");
            }

            var handle = OpenNoFollow(canonicalPath);
            try
            {
                // libc open(2) does not create a .NET async-capable handle. FileStream still provides its async APIs
                // over this synchronous handle while retaining ownership and, critically, reading the exact validated
                // descriptor used for identity capture.
                var stream = new FileStream(handle, FileAccess.Read, bufferSize: 81920, isAsync: false);
                try
                {
                    var identity = CaptureIdentity(handle, canonicalPath, stream.Length);
                    var lastWriteUtc = File.GetLastWriteTimeUtc(canonicalPath);
                    if (stream.Length <= 0)
                    {
                        throw new GgufImportException(GgufImportRejectionCode.InvalidSource, "The selected source is empty.");
                    }

                    return new ValidatedGgufImportSource(canonicalPath, stream, identity, lastWriteUtc);
                }
                catch
                {
                    stream.Dispose();
                    throw;
                }
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
        catch (GgufImportException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new GgufImportException(GgufImportRejectionCode.InvalidSource,
                "The selected source could not be opened safely.",
                exception);
        }
    }

    public void Rewind() => _stream.Position = 0;

    public void VerifyStillCurrent()
    {
        try
        {
            EnsureNoReparseComponents(_canonicalPath);
            using var currentHandle = OpenNoFollow(_canonicalPath);
            var currentLength = RandomAccess.GetLength(currentHandle);
            var current = CaptureIdentity(currentHandle, _canonicalPath, currentLength);
            if (current != _identity || currentLength != Length || File.GetLastWriteTimeUtc(_canonicalPath) != _lastWriteUtc)
            {
                throw new GgufImportException(GgufImportRejectionCode.InvalidSource, "The selected source changed while it was being copied.");
            }
        }
        catch (GgufImportException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new GgufImportException(GgufImportRejectionCode.InvalidSource,
                "The selected source could not be revalidated safely.",
                exception);
        }
    }

    public ValueTask DisposeAsync() => _stream.DisposeAsync();

    private static void EnsureNoReparseComponents(string canonicalPath)
    {
        FileSystemInfo? current = new FileInfo(canonicalPath);
        while (current is not null)
        {
            current.Refresh();
            if (current.LinkTarget is not null || current.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new GgufImportException(GgufImportRejectionCode.InvalidSource,
                    "The selected source path contains a symbolic link or reparse point.");
            }

            current = current switch
            {
                FileInfo file => file.Directory,
                DirectoryInfo directory => directory.Parent,
                _ => null
            };
        }
    }

    private static SafeFileHandle OpenNoFollow(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            return File.OpenHandle(path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }

        var pathBytes = new byte[Encoding.UTF8.GetByteCount(path) + 1];
        Encoding.UTF8.GetBytes(path, pathBytes);
        var descriptor = open(pathBytes, LinuxReadOnlyNoFollowNonBlockingCloseOnExec);
        if (descriptor < 0)
        {
            throw new UnauthorizedAccessException(string.Create(CultureInfo.InvariantCulture,
                $"The selected source could not be opened without following links (errno {Marshal.GetLastPInvokeError()})."));
        }

        return new SafeFileHandle(descriptor, ownsHandle: true);
    }

    [SuppressMessage("Sonar Code Smell", "S3869:SafeHandle instances should not use DangerousGetHandle",
        Justification = "Linux /proc exposes identity for the live owned descriptor; the handle is never released or transferred here.")]
    private static SourceIdentity CaptureIdentity(SafeFileHandle handle, string canonicalPath, long length)
    {
        if (OperatingSystem.IsLinux())
        {
            var descriptor = handle.DangerousGetHandle().ToInt64();
            var openedPath = new FileInfo(string.Create(CultureInfo.InvariantCulture, $"/proc/self/fd/{descriptor}"))
                            .ResolveLinkTarget(returnFinalTarget: true)?.FullName;
            if (!string.Equals(openedPath, canonicalPath, StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException("The selected source handle did not resolve to the validated path.");
            }

            var fields = File.ReadAllLines(string.Create(CultureInfo.InvariantCulture, $"/proc/self/fdinfo/{descriptor}"));
            var mount = ReadField(fields, "mnt_id:");
            var inode = ReadField(fields, "ino:");
            if (mount is not null && inode is not null)
            {
                return new SourceIdentity("linux", mount, inode, length);
            }
        }
        else if (OperatingSystem.IsWindows())
        {
            if (!GetFileInformationByHandle(handle, out var information))
            {
                throw new UnauthorizedAccessException("The selected source handle identity could not be read.");
            }

            return new SourceIdentity("windows",
                information.VolumeSerialNumber.ToString(CultureInfo.InvariantCulture),
                string.Create(CultureInfo.InvariantCulture, $"{information.FileIndexHigh:x8}{information.FileIndexLow:x8}"),
                length);
        }

        return new SourceIdentity("best-effort", canonicalPath, File.GetLastWriteTimeUtc(canonicalPath).Ticks.ToString(CultureInfo.InvariantCulture), length);
    }

    private static string? ReadField(IEnumerable<string> lines, string prefix)
    {
        return lines.FirstOrDefault(line => line.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..].Trim();
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int open(byte[] pathname, int flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    private sealed record SourceIdentity(string Platform, string Volume, string FileId, long Length);
}
