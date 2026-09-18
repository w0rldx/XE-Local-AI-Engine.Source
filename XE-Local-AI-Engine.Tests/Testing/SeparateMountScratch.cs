namespace XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     A scratch directory on a mount that is NOT the root filesystem, together with a required-bytes threshold that
///     tells the two apart.
///     <para>
///         Every free-disk guard in the node measures a directory the operator can redirect — a build cache, a models
///         volume — onto a mount of their choosing. Handing <see cref="Path.GetPathRoot(string)" /> to
///         <see cref="DriveInfo" /> silently measured the wrong one, because on Linux every absolute path roots at
///         <c>/</c>. A test can only catch that with a real mount the answer differs on, so this probes for one
///         instead of guessing: <c>/dev/shm</c> and <c>/tmp</c> are the two that are usually their own tmpfs.
///     </para>
///     <para>
///         <see cref="RequiredMarginBytes" /> exists because free space moves between two reads. The threshold sits at
///         the midpoint of the two figures, so a margin of 1 GiB leaves half a gigabyte of slack on each side and no
///         plausible drift can flip the answer. A host whose candidate mounts are within that margin of <c>/</c>
///         cannot distinguish a correct measurement from a wrong one, and the test skips visibly rather than passing
///         for the wrong reason.
///     </para>
/// </summary>
internal sealed class SeparateMountScratch : IDisposable
{
    /// <summary>How far apart the two filesystems' free space must be before a threshold between them is meaningful.</summary>
    private const long RequiredMarginBytes = 1024L * 1024 * 1024;

    private static readonly string[] Candidates = ["/dev/shm", "/tmp"];

    private SeparateMountScratch(string path, long availableFreeBytes, long rootAvailableFreeBytes)
    {
        Path = path;
        AvailableFreeBytes = availableFreeBytes;
        RootAvailableFreeBytes = rootAvailableFreeBytes;
    }

    /// <summary>The scratch directory itself — hand this to the component under test as its cache/data root.</summary>
    public string Path { get; }

    /// <summary>Free space on the mount actually holding <see cref="Path" />: what a correct measurement reports.</summary>
    public long AvailableFreeBytes { get; }

    /// <summary>Free space on <c>/</c>: what a path-root measurement reports instead.</summary>
    public long RootAvailableFreeBytes { get; }

    /// <summary>
    ///     A required-bytes floor strictly between the two figures. A guard measuring the right mount answers
    ///     <see cref="ExpectedSatisfied" />; one measuring <c>/</c> answers its opposite.
    /// </summary>
    public long ThresholdBetweenBytes =>
        Math.Min(AvailableFreeBytes, RootAvailableFreeBytes) + (Math.Abs(AvailableFreeBytes - RootAvailableFreeBytes) / 2);

    /// <summary>What a guard with a <see cref="ThresholdBetweenBytes" /> floor must report for <see cref="Path" />.</summary>
    public bool ExpectedSatisfied => AvailableFreeBytes >= ThresholdBetweenBytes;

    /// <summary>The assertion message: says which filesystem answered, not merely that a bool was wrong.</summary>
    public string WrongMountMessage =>
        $"The free-disk check measured the root filesystem ({RootAvailableFreeBytes} bytes free) instead of the mount "
        + $"holding '{Path}' ({AvailableFreeBytes} bytes free), against a {ThresholdBetweenBytes}-byte floor.";

    /// <summary>
    ///     Creates the scratch directory on the first usable candidate mount, or skips the calling test with the
    ///     reason no candidate could serve.
    /// </summary>
    /// <param name="prefix">Leading name segment, so a stray directory says which test left it.</param>
    public static SeparateMountScratch CreateOrSkip(string prefix)
    {
        var scratch = TryCreate(prefix, out var reason);

        // Skip.Unless never returns when the condition is false, which the compiler knows, so the null case does not
        // reach the caller.
        Skip.Unless(scratch is not null, reason);
        return scratch;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort scratch cleanup; a leftover directory on a tmpfs is not a test failure.
        }
    }

    private static SeparateMountScratch? TryCreate(string prefix, out string reason)
    {
        if (!OperatingSystem.IsLinux())
        {
            reason = "Telling a mount apart from the root filesystem by free space is a Linux-only arrangement here; "
                     + "on Windows DriveInfo normalises a directory to its volume root and the two answers coincide.";
            return null;
        }

        var rootAvailable = new DriveInfo("/").AvailableFreeSpace;
        foreach (var candidate in Candidates)
        {
            if (!Directory.Exists(candidate))
            {
                continue;
            }

            var path = System.IO.Path.Combine(candidate, $"{prefix}-{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            // Measured after the directory exists, so this is the same filesystem the component under test will see.
            var available = new DriveInfo(path).AvailableFreeSpace;
            if (Math.Abs(available - rootAvailable) >= RequiredMarginBytes)
            {
                reason = string.Empty;
                return new SeparateMountScratch(path, available, rootAvailable);
            }

            var scratch = new SeparateMountScratch(path, available, rootAvailable);
            scratch.Dispose();
        }

        reason = $"No candidate mount ({string.Join(", ", Candidates)}) has free space at least {RequiredMarginBytes} "
                 + $"bytes away from the root filesystem's {rootAvailable}, so a measurement of the wrong filesystem "
                 + "would be indistinguishable from a correct one on this host.";
        return null;
    }
}
