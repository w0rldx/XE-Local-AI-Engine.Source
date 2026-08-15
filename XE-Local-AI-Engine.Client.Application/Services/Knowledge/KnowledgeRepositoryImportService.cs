namespace XE_Local_AI_Engine.Client.Services.Knowledge;

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.Win32.SafeHandles;
using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Client.Services.DocumentIngestion;
using XE_Local_AI_Engine.Client.Services.Workspace;
using XE_Local_AI_Engine.Client.Services.Workspace.Implementation;

/// <summary>
///     Bounded repository importer. The request carries only an opaque registered-folder id; host paths are resolved
///     internally, revalidated as canonical Git roots, and never persisted. Git supplies the tracked + unignored file
///     set, while the product's credential/generated-file exclusions and reparse checks are applied again before read.
/// </summary>
public sealed class KnowledgeRepositoryImportService : IKnowledgeRepositoryImportService
{
    private const string SourceKind = "repository";
    private const string DefaultMimeType = "text/plain";
    private const int ReadOnlyNoFollowCloseOnExecFlags = 0x0 | 0x20000 | 0x80000;

    private readonly IDevelopmentRepositoryBindingService _repositories;
    private readonly ISensitiveFileExclusionService _exclusions;
    private readonly IKnowledgeDocumentBlobStore _blobStore;
    private readonly IKnowledgeIngestionDispatcher _dispatcher;
    private readonly IKnowledgeDocumentCatalogService _catalog;
    private readonly IKnowledgeDocumentPurgeService _purge;
    private readonly IDocumentTextExtractor _extractor;
    private readonly KnowledgeBaseOptions _options;

    public KnowledgeRepositoryImportService(IDevelopmentRepositoryBindingService repositories,
        ISensitiveFileExclusionService exclusions,
        IKnowledgeDocumentBlobStore blobStore,
        IKnowledgeIngestionDispatcher dispatcher,
        IKnowledgeDocumentCatalogService catalog,
        IKnowledgeDocumentPurgeService purge,
        IDocumentTextExtractor extractor,
        IOptions<KnowledgeBaseOptions> options)
    {
        _repositories = repositories ?? throw new ArgumentNullException(nameof(repositories));
        _exclusions = exclusions ?? throw new ArgumentNullException(nameof(exclusions));
        _blobStore = blobStore ?? throw new ArgumentNullException(nameof(blobStore));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _purge = purge ?? throw new ArgumentNullException(nameof(purge));
        _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    public async Task<KnowledgeRepositoryImportResult> ImportAsync(Guid selectedFolderId,
        string? collectionId,
        CancellationToken cancellationToken)
    {
        var binding = await _repositories.ResolveFolderAsync(selectedFolderId, cancellationToken).ConfigureAwait(false);
        var resolvedRoot = HostPathSafety.TryResolveTrustedRoot(binding.RepositoryRoot)
                           ?? throw new KnowledgeRepositoryImportRejectedException("The registered repository is unavailable or unsafe.");
        var derivedCollection = string.IsNullOrWhiteSpace(collectionId)
            ? string.Concat("REPO-", selectedFolderId.ToString("N"))
            : collectionId;
        if (!KnowledgeCollectionScope.TryNormalize(derivedCollection, out var normalizedCollection))
        {
            throw new ArgumentException("The knowledge collection id is invalid.", nameof(collectionId));
        }

        var repositorySourceId = binding.SelectedFolderId.ToString("N");

        var files = await ListRepositoryFilesAsync(resolvedRoot, cancellationToken).ConfigureAwait(false);
        if (files.Count > Math.Max(1, _options.MaxRepositoryImportFiles))
        {
            throw new KnowledgeRepositoryImportRejectedException("The repository contains more supported files than one import permits.");
        }

        var added = 0;
        var updated = 0;
        var deduplicated = 0;
        var enqueued = 0;
        var skipped = 0;
        var queueFull = false;
        long admittedBytes = 0;
        var admittedSourcePaths = new HashSet<string>(StringComparer.Ordinal);
        var maxAggregateBytes = Math.Max(1L, _options.MaxRepositoryImportBytes);
        var maxFileBytes = Math.Max(1L, _options.MaxRepositoryImportFileBytes);
        foreach (var relativePath in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var extension = Path.GetExtension(relativePath);
            if (!_extractor.IsSupported(extension) || IsExcluded(relativePath))
            {
                skipped++;
                continue;
            }

            var fullPath = Path.GetFullPath(relativePath, resolvedRoot);
            if (!HostPathSafety.IsPathWithinRoot(resolvedRoot, fullPath))
            {
                skipped++;
                continue;
            }

            if (!HasOnlyRegularPathComponents(resolvedRoot, fullPath))
            {
                skipped++;
                continue;
            }

            var remainingBytes = maxAggregateBytes - admittedBytes;
            if (remainingBytes <= 0)
            {
                throw new KnowledgeRepositoryImportRejectedException("The repository exceeds the source-byte limit for one import.");
            }

            var bytes = await ReadFileUnderGuardAsync(fullPath,
                    resolvedRoot,
                    Math.Min(maxFileBytes, remainingBytes),
                    cancellationToken)
                .ConfigureAwait(false);
            admittedBytes = checked(admittedBytes + bytes.LongLength);
            if (admittedBytes > maxAggregateBytes)
            {
                throw new KnowledgeRepositoryImportRejectedException("The repository exceeds the source-byte limit for one import.");
            }

            var normalizedSourcePath = NormalizeRelativePath(relativePath);
            admittedSourcePaths.Add(normalizedSourcePath);
            var documentId = Guid.NewGuid();
            var input = new KnowledgeDocumentInput(documentId,
                relativePath,
                DefaultMimeType,
                extension,
                bytes.LongLength,
                Convert.ToHexString(SHA256.HashData(bytes)),
                bytes,
                _options.EmbeddingModelName,
                normalizedCollection,
                normalizedSourcePath,
                SourceKind,
                repositorySourceId);
            var result = await _blobStore.AddAsync(input, cancellationToken).ConfigureAwait(false);
            if (result.WasInserted)
            {
                added++;
            }
            else if (result.WasUpdated)
            {
                updated++;
            }
            else
            {
                deduplicated++;
            }

            var status = await _catalog.GetStatusAsync(result.DocumentId, cancellationToken).ConfigureAwait(false)
                         ?? KnowledgeDocumentStatus.Pending;
            if (!result.WasInserted && !result.WasUpdated
                                    && status is not (KnowledgeDocumentStatus.Pending or KnowledgeDocumentStatus.Failed))
            {
                continue;
            }

            var admission = await _dispatcher.EnqueueAsync(result.DocumentId, cancellationToken).ConfigureAwait(false);
            if (admission == KnowledgeIngestionEnqueueResult.QueueFull)
            {
                queueFull = true;
                break;
            }

            if (admission == KnowledgeIngestionEnqueueResult.Accepted)
            {
                enqueued++;
            }
        }

        var removed = 0;
        if (!queueFull)
        {
            var existingDocuments = await _catalog.ListAsync(normalizedCollection,
                                                      SourceKind,
                                                      repositorySourceId,
                                                      cancellationToken)
                                                  .ConfigureAwait(false);
            foreach (var existing in existingDocuments)
            {
                if (!string.Equals(existing.SourceKind, SourceKind, StringComparison.Ordinal)
                    || existing.SourcePath is null
                    || admittedSourcePaths.Contains(NormalizeRelativePath(existing.SourcePath)))
                {
                    continue;
                }

                if (await _purge.PurgeAsync(existing.DocumentId, cancellationToken).ConfigureAwait(false))
                {
                    removed++;
                }
            }
        }

        return new KnowledgeRepositoryImportResult(normalizedCollection,
            files.Count,
            added,
            deduplicated,
            enqueued,
            skipped,
            queueFull,
            updated,
            removed);
    }

    private static async Task<IReadOnlyList<string>> ListRepositoryFilesAsync(string root, CancellationToken cancellationToken)
    {
        var git = new HostGitRunner(timeoutSeconds: 60);
        var result = await git.RunAsync(root,
            AgentHomeGit.Arguments("ls-files", "--cached", "--others", "--exclude-standard", "-z"),
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new KnowledgeRepositoryReadException("The registered repository file index could not be read.");
        }

        return result.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries)
                     .Where(IsSafeRelativePath)
                     .Distinct(StringComparer.Ordinal)
                     .Order(StringComparer.Ordinal)
                     .ToList();
    }

    private bool IsExcluded(string relativePath)
    {
        return NormalizeRelativePath(relativePath).Split('/', StringSplitOptions.RemoveEmptyEntries)
                                                  .Any(segment => _exclusions.IsExcluded(segment, isDirectory: false));
    }

    private static bool IsSafeRelativePath(string value)
    {
        return !string.IsNullOrWhiteSpace(value)
               && !Path.IsPathFullyQualified(value)
               && !value.Any(char.IsControl)
               && value.Replace(oldChar: '\\', newChar: '/').Split('/').All(static segment => segment is not ("" or "." or ".."));
    }

    private static string NormalizeRelativePath(string value)
    {
        return value.Replace(oldChar: '\\', newChar: '/').Normalize(NormalizationForm.FormC);
    }

    private static bool HasOnlyRegularPathComponents(string resolvedRoot, string fullPath)
    {
        try
        {
            var relativePath = Path.GetRelativePath(resolvedRoot, fullPath);
            var segments = relativePath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            var currentPath = resolvedRoot;
            for (var index = 0; index < segments.Length; index++)
            {
                currentPath = Path.Combine(currentPath, segments[index]);
                FileSystemInfo info = index == segments.Length - 1
                    ? new FileInfo(currentPath)
                    : new DirectoryInfo(currentPath);
                if (!info.Exists || HostPathSafety.IsReparsePoint(info))
                {
                    return false;
                }
            }

            return segments.Length > 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task<byte[]> ReadFileUnderGuardAsync(string fullPath,
        string resolvedRoot,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        try
        {
            using var handle = OpenNoFollow(fullPath);
            EnsureOpenedFileWithinRoot(handle, resolvedRoot);
            var length = RandomAccess.GetLength(handle);
            if (length < 0 || length > maximumBytes || length > int.MaxValue)
            {
                throw new KnowledgeRepositoryImportRejectedException("A repository file exceeds the configured per-file or aggregate byte limit.");
            }

            var content = new byte[(int)length];
            var read = 0;
            while (read < content.Length)
            {
                var count = await RandomAccess.ReadAsync(handle, content.AsMemory(read), read, cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    return content[..read];
                }

                read += count;
            }

            Memory<byte> probe = new byte[1];
            if (await RandomAccess.ReadAsync(handle, probe, length, cancellationToken).ConfigureAwait(false) > 0)
            {
                throw new KnowledgeRepositoryReadException("A repository file grew while it was being read.");
            }

            return content;
        }
        catch (IOException exception)
        {
            throw new KnowledgeRepositoryReadException("A repository file could not be read safely.", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new KnowledgeRepositoryReadException("A repository file could not be opened safely.", exception);
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
                FileOptions.Asynchronous | FileOptions.RandomAccess);
        }

        var pathBytes = new byte[Encoding.UTF8.GetByteCount(path) + 1];
        Encoding.UTF8.GetBytes(path, pathBytes);
        var fileDescriptor = open(pathBytes, ReadOnlyNoFollowCloseOnExecFlags);
        if (fileDescriptor < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            throw new UnauthorizedAccessException(string.Create(CultureInfo.InvariantCulture,
                $"A repository file could not be opened without following links (errno {error})."));
        }

        return new SafeFileHandle(fileDescriptor, ownsHandle: true);
    }

    [SuppressMessage("Sonar Code Smell", "S3869:SafeHandle instances should not use DangerousGetHandle",
        Justification =
            "Linux exposes an opened file's canonical target through /proc/self/fd/{fd}; the owning SafeFileHandle remains live for this synchronous lookup and is never released or transferred here.")]
    private static void EnsureOpenedFileWithinRoot(SafeFileHandle handle, string resolvedRoot)
    {
        string? openedPath;
        if (OperatingSystem.IsLinux())
        {
            var descriptorPath = string.Create(CultureInfo.InvariantCulture,
                $"/proc/self/fd/{handle.DangerousGetHandle().ToInt64()}");
            openedPath = new FileInfo(descriptorPath).ResolveLinkTarget(returnFinalTarget: true)?.FullName;
        }
        else if (OperatingSystem.IsWindows())
        {
            var buffer = new char[32_768];
            var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, flags: 0);
            if (length == 0 || length >= (uint)buffer.Length)
            {
                throw new UnauthorizedAccessException("A repository file's opened path could not be verified.");
            }

            openedPath = NormalizeWindowsHandlePath(new string(buffer, 0, (int)length));
        }
        else
        {
            // Linux and Windows are the supported/default hosts. Other platforms retain the pre-open component walk,
            // but have no native final-handle canonicalization in this implementation.
            return;
        }

        if (openedPath is null
            || !HostPathSafety.IsPathWithinRoot(resolvedRoot,
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(openedPath))))
        {
            throw new UnauthorizedAccessException("A repository file resolved outside the registered repository root.");
        }
    }

    private static string NormalizeWindowsHandlePath(string path)
    {
        const string devicePrefix = @"\\?\";
        const string uncPrefix = @"\\?\UNC\";
        if (path.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return string.Concat(@"\\", path[uncPrefix.Length..]);
        }

        return path.StartsWith(devicePrefix, StringComparison.Ordinal) ? path[devicePrefix.Length..] : path;
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int open(byte[] pathname, int flags);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file,
        [Out]
        char[] filePath,
        uint filePathSize,
        uint flags);
}
