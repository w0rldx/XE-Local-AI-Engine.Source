namespace XE_Local_AI_Engine.Client.Services.ExternalApps;

using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     The on-disk shape of one installed application instance, and the only code that creates, materialises or
///     deletes anything under it.
///     <para>
///         Both halves are namespaced BY SERVICE. <c>storage[].name</c> is unique only within a service — an
///         application can declare <c>data</c> on two of them — so a flat layout would bind one host directory into
///         two containers holding different data.
///     </para>
/// </summary>
/// <remarks>
///     Lexical confinement is not confinement: nothing in a path string resolves a symlink, so a reparse point on any
///     component redirects a valid-looking path out of the instance directory. Every component from
///     <c>external-apps</c> down is therefore checked for a link before it is created, written or handed out as a
///     bind source, and every write goes through a temp file opened <see cref="FileMode.CreateNew" /> rather than an
///     in-place overwrite.
/// </remarks>
internal sealed class ExternalAppStorageLayout
{
    /// <summary>The first path component under the instance root; also where the link checks start.</summary>
    internal const string FeatureDirectoryName = "external-apps";

    /// <summary>The directory holding one subdirectory per instance.</summary>
    internal const string InstancesDirectoryName = "instances";

    /// <summary>The per-instance directory holding the writable, per-service <c>storage[]</c> mounts.</summary>
    internal const string VolumesDirectoryName = "volumes";

    /// <summary>The per-instance directory holding the read-only, per-service <c>files[]</c> assets.</summary>
    internal const string FilesDirectoryName = "files";

    private const UnixFileMode PrivateDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const int MaxTempAttempts = 8;

    private readonly string _root;

    public ExternalAppStorageLayout(INodeDataDirectory dataDirectory, IOptions<ExternalAppsOptions> options)
    {
        ArgumentNullException.ThrowIfNull(dataDirectory);
        ArgumentNullException.ThrowIfNull(options);

        var configured = options.Value.InstanceRoot;
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(string.IsNullOrWhiteSpace(configured) ? dataDirectory.Root : configured));
    }

    /// <summary>The absolute directory instance directories are created under. Never itself created by this type.</summary>
    public string Root => _root;

    /// <summary>The paths one instance uses, without touching the disk. Safe to call for an instance that was never installed.</summary>
    public ExternalAppStoragePaths Describe(Guid instanceId)
    {
        var instanceRoot = Path.Combine(_root, FeatureDirectoryName, InstancesDirectoryName, instanceId.ToString("N", CultureInfo.InvariantCulture));
        return new ExternalAppStoragePaths
        {
            InstanceRoot = instanceRoot,
            VolumesRoot = Path.Combine(instanceRoot, VolumesDirectoryName),
            FilesRoot = Path.Combine(instanceRoot, FilesDirectoryName)
        };
    }

    /// <summary>
    ///     Creates every directory the manifest's services need and materialises their <c>files[]</c>, then returns
    ///     the paths a deployment plan binds.
    ///     <para>
    ///         Idempotent by contract: install, start after a configure, update and reset all re-enter it, so a
    ///         pre-existing file is a case to verify rather than a violation to refuse. A file whose content already
    ///         hashes to the manifest's <c>sha256</c> is reused untouched; one that does not is replaced through a
    ///         temp file and an atomic rename. It must run only while no container of the instance exists.
    ///     </para>
    /// </summary>
    public async Task<ExternalAppStoragePaths> PrepareAsync(Guid instanceId, ApplicationManifest manifest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var paths = Describe(instanceId);
        EnsureDirectory(paths.InstanceRoot);

        foreach (var service in manifest.Services)
        {
            foreach (var storage in service.Storage)
            {
                EnsureDirectory(paths.VolumePath(service.Name, ValidateRelativeName(storage.Name, "storage[].name")));
            }

            foreach (var file in service.Files)
            {
                await MaterializeAsync(paths, service.Name, file, cancellationToken);
            }
        }

        return paths;
    }

    /// <summary>
    ///     Deletes the instance's writable volumes and nothing else — the reset contract. <c>files/</c> survives, and
    ///     the caller re-enters <see cref="PrepareAsync" /> afterwards, which re-verifies every surviving asset rather than
    ///     trusting it.
    /// </summary>
    public void DeleteVolumes(Guid instanceId)
    {
        var paths = Describe(instanceId);
        if (!Directory.Exists(paths.VolumesRoot))
        {
            return;
        }

        EnsureNoLinksOnPath(paths.VolumesRoot);
        try
        {
            Directory.Delete(paths.VolumesRoot, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ExternalAppStorageException($"The instance's volumes directory '{paths.VolumesRoot}' could not be deleted.", exception);
        }
    }

    /// <summary>
    ///     Deletes the whole instance directory. Best-effort about I/O by contract: an uninstall that cannot remove a
    ///     directory still removes the rows, so the caller is told what happened rather than left with an instance it
    ///     cannot get rid of. A LINK on the path is not an I/O failure and is not reported as one — it is refused with
    ///     an <see cref="ExternalAppStorageException" />, because a recursive delete that followed it would remove a
    ///     tree this feature does not own.
    /// </summary>
    public bool Delete(Guid instanceId)
    {
        var paths = Describe(instanceId);
        if (!Directory.Exists(paths.InstanceRoot))
        {
            return true;
        }

        // The same per-component check DeleteVolumes makes, and for the same reason: a directory link planted at any
        // ancestor inside the feature root — at 'external-apps/instances', say — redirects this recursive delete out
        // of the instance directory entirely.
        EnsureNoLinksOnPath(paths.InstanceRoot);

        try
        {
            Directory.Delete(paths.InstanceRoot, recursive: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    ///     Re-validates the instance's volumes directory and answers whether anything is still in it: the path when
    ///     entries remain, <see langword="null" /> when the directory is absent or already empty.
    ///     <para>
    ///         The same per-component no-follow walk <see cref="DeleteVolumes" /> makes, and for the same reason —
    ///         the answer becomes the bind source of a container that runs as root over it, so a link on any
    ///         component would hand that container a tree this feature does not own.
    ///     </para>
    ///     <para>
    ///         One method and two uses: called before the helper runs it says whether a helper is needed at all, and
    ///         called after it the same non-null answer means the wipe did not finish. Only the TOP level is read,
    ///         which is exactly what the engine can always read — <c>volumes/</c> is engine-created and engine-owned,
    ///         and a subdirectory an application made unreadable is precisely what the helper exists for.
    ///     </para>
    /// </summary>
    internal string? FindVolumeContents(Guid instanceId)
    {
        var paths = Describe(instanceId);
        if (!Directory.Exists(paths.VolumesRoot))
        {
            return null;
        }

        EnsureNoLinksOnPath(paths.VolumesRoot);

        try
        {
            return Directory.EnumerateFileSystemEntries(paths.VolumesRoot).Any() ? paths.VolumesRoot : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ExternalAppStorageException($"The instance's volumes directory '{paths.VolumesRoot}' could not be read.", exception);
        }
    }

    /// <summary>
    ///     Re-validates every bind source of a plan, immediately before the daemon is asked to create the container
    ///     that binds them.
    ///     <para>
    ///         <see cref="PrepareAsync" /> already checked these components, but a plan crosses asynchronous daemon calls
    ///         and a path string resolves nothing: a component replaced by a link in between would hand the daemon a
    ///         bind source outside the instance directory, and nothing downstream would notice.
    ///     </para>
    /// </summary>
    internal void VerifyBindSources(DeploymentPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        foreach (var hostPath in plan.Services.SelectMany(static service => service.Specification.Mounts)
                                     .Select(static mount => mount.HostPath))
        {
            // Relativize refuses anything outside the instance root outright; the per-component walk refuses a path
            // that is lexically inside it and physically somewhere else.
            EnsureNoLinksOnPath(hostPath);
        }
    }

    /// <summary>
    ///     Validates one <c>storage[].name</c> or <c>files[].source</c> as a relative, single-rooted, traversal-free
    ///     path and returns it with this platform's separators. The catalog validator states the same rule; this one
    ///     is the control, because an installed snapshot can predate the validator that admitted it.
    /// </summary>
    internal static string ValidateRelativeName(string? value, string what)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ExternalAppStorageException($"A {what} is required.");
        }

        if (value.Contains('\0', StringComparison.Ordinal) || value.Contains(':', StringComparison.Ordinal) || Path.IsPathRooted(value))
        {
            throw new ExternalAppStorageException($"The {what} '{value}' must be a relative path without a drive or root.");
        }

        var segments = value.Split(['/', '\\'], StringSplitOptions.None);
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment == "." || segment == "..")
            {
                throw new ExternalAppStorageException($"The {what} '{value}' must not contain an empty or traversing segment.");
            }
        }

        return Path.Combine(segments);
    }

    private async Task MaterializeAsync(ExternalAppStoragePaths paths, string serviceName, ApplicationFile file, CancellationToken cancellationToken)
    {
        var relative = ValidateRelativeName(file.Source, "files[].source");
        var target = paths.FilePath(serviceName, relative);
        var expected = file.Sha256 ?? string.Empty;

        byte[] content;
        try
        {
            content = Convert.FromBase64String(file.ContentBase64 ?? string.Empty);
        }
        catch (FormatException exception)
        {
            throw new ExternalAppStorageException($"The catalog asset '{file.Source}' is not valid base64.", exception);
        }

        var declared = Convert.ToHexStringLower(SHA256.HashData(content));
        if (!string.Equals(declared, expected, StringComparison.Ordinal))
        {
            // The manifest disagrees with itself. Writing the bytes anyway would put content on disk that no
            // fingerprint covers, and the reuse branch below would then treat it as verified on every later pass.
            throw new ExternalAppStorageException($"The catalog asset '{file.Source}' hashes to '{declared}', not to the '{expected}' the manifest declares.");
        }

        EnsureDirectory(Path.GetDirectoryName(target)!);

        if (File.Exists(target))
        {
            // The LEAF, before its hash is read. EnsureDirectory cleared every parent component, but a symlink at
            // the target itself hashes to whatever it points at — so a link planted here would hash correctly and
            // then be handed to the daemon as a read-only bind source pointing outside the instance directory.
            EnsureNotALink(target);

            if (string.Equals(await HashFileAsync(target, cancellationToken), expected, StringComparison.Ordinal))
            {
                return;
            }
        }

        await WriteThroughTempFileAsync(target, content, expected, cancellationToken);
    }

    private async Task WriteThroughTempFileAsync(string target, byte[] content, string expected, CancellationToken cancellationToken)
    {
        var temporary = await CreateTempFileAsync(target, content, cancellationToken);
        try
        {
            EnsureNoLinksOnPath(target);
            File.Move(temporary, target, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ExternalAppStorageException($"The catalog asset could not be written to '{target}'.", exception);
        }
        finally
        {
            TryDeleteTempFile(temporary);
        }

        if (!string.Equals(await HashFileAsync(target, cancellationToken), expected, StringComparison.Ordinal))
        {
            throw new ExternalAppStorageException($"The catalog asset written to '{target}' does not hash to the value the manifest declares.");
        }
    }

    private static async Task<string> CreateTempFileAsync(string target, byte[] content, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxTempAttempts; attempt++)
        {
            var candidate = target + ".tmp-" + attempt.ToString(CultureInfo.InvariantCulture);
            try
            {
                // CreateNew + FileShare.None is what keeps the write from following a link someone else planted.
                await using (var stream = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await stream.WriteAsync(content, cancellationToken);
                }

                SecureFilePermissions.Apply(candidate);
                return candidate;
            }
            catch (IOException) when (File.Exists(candidate))
            {
                // A temp file from an interrupted pass, or a concurrent one. Try the next name rather than
                // overwriting: CreateNew is what keeps this write from following a link someone else left behind.
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new ExternalAppStorageException($"A temporary file for '{target}' could not be created.", exception);
            }
        }

        throw new ExternalAppStorageException($"No temporary file name was free for '{target}' after {MaxTempAttempts.ToString(CultureInfo.InvariantCulture)} attempts.");
    }

    private static void TryDeleteTempFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The rename consumed it, or the directory is gone with it. Either way there is nothing to clean up and
            // nothing a caller could do about it.
        }
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ExternalAppStorageException($"The file '{path}' could not be read back for verification.", exception);
        }
    }

    /// <summary>
    ///     Creates <paramref name="path" /> and every missing component beneath the instance root, checking each one
    ///     for a link on the way down and narrowing each created directory to <c>0700</c>.
    /// </summary>
    private void EnsureDirectory(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var relative = Relativize(full);

        var current = _root;
        foreach (var segment in relative)
        {
            current = Path.Combine(current, segment);
            EnsureNotALink(current);

            if (Directory.Exists(current))
            {
                continue;
            }

            try
            {
                _ = Directory.CreateDirectory(current);
                if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                {
                    File.SetUnixFileMode(current, PrivateDirectoryMode);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                throw new ExternalAppStorageException($"The instance directory '{current}' could not be created.", exception);
            }
        }
    }

    /// <summary>Checks every existing component of <paramref name="path" /> beneath the instance root for a link.</summary>
    private void EnsureNoLinksOnPath(string path)
    {
        var current = _root;
        foreach (var segment in Relativize(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))))
        {
            current = Path.Combine(current, segment);
            EnsureNotALink(current);
        }
    }

    private static void EnsureNotALink(string path)
    {
        // Both types are asked, because a link's own kind is not knowable before it is resolved: a symlink to a
        // directory answers DirectoryInfo and a dangling one answers neither Exists.
        if (new DirectoryInfo(path).LinkTarget is not null || new FileInfo(path).LinkTarget is not null)
        {
            throw new ExternalAppStorageException($"The path component '{path}' is a link. Application storage is refused rather than followed out of the instance directory.");
        }
    }

    private string[] Relativize(string full)
    {
        var prefix = _root.EndsWith(Path.DirectorySeparatorChar) ? _root : _root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new ExternalAppStorageException($"The path '{full}' resolves outside the instance root '{_root}'.");
        }

        return full[prefix.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
    }
}

/// <summary>Where one instance's per-service bind sources live on this host.</summary>
internal sealed class ExternalAppStoragePaths
{
    public required string InstanceRoot { get; init; }

    public required string VolumesRoot { get; init; }

    public required string FilesRoot { get; init; }

    /// <summary>The host directory behind one service's <c>storage[]</c> entry.</summary>
    public string VolumePath(string serviceName, string storageName)
    {
        return Path.Combine(VolumesRoot, serviceName, storageName);
    }

    /// <summary>The host path behind one service's <c>files[]</c> entry.</summary>
    public string FilePath(string serviceName, string relativeSource)
    {
        return Path.Combine(FilesRoot, serviceName, relativeSource);
    }
}

/// <summary>
///     An application's storage could not be created, verified or removed as the engine requires. An
///     <see cref="IOException" /> because that is what it is; a named subtype because the pipelines have to tell a
///     refusal of theirs apart from a disk that filled up, and both become the same failure category to the user.
/// </summary>
public sealed class ExternalAppStorageException : IOException
{
    public ExternalAppStorageException(string message) : base(message)
    {
    }

    public ExternalAppStorageException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public ExternalAppStorageException()
    {
    }
}
