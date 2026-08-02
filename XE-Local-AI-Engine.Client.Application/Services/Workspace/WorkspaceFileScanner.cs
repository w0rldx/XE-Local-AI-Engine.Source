namespace XE_Local_AI_Engine.Client.Services.Workspace;

using System.Globalization;
using System.IO.Enumeration;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
///     The engine's own implementation of the two read-only workspace surveys the coder model uses —
///     <c>list_files</c> and <c>search_text</c> — over the managed workspace on the host filesystem.
///     <para>
///         <b>Why this is managed code rather than a shell-out, and why that decision is not a Windows special case.</b>
///         Both operations used to run the bare executables <c>find</c> and <c>grep</c> with POSIX-only argument
///         vectors, and the file carried no OS branch at all. On a stock Windows 11 install <c>grep</c> does not exist,
///         and <c>find</c> resolves to <c>C:\Windows\System32\find.exe</c> — the DOS tool, which rejects
///         <c>-maxdepth</c>, <c>-iname</c> and <c>-prune</c>. <c>System32</c> normally precedes Git for Windows'
///         <c>usr\bin</c> on <c>PATH</c>, so this happens even where GNU find is installed.
///     </para>
///     <para>
///         The alternative — resolve a known-good toolchain and refuse loudly when it is absent — was rejected. A
///         shipped RC cannot assume Git for Windows is installed, so on the machines this is meant to fix that design
///         converts a broken feature into a disabled one; it also leaves the engine's behaviour a function of whatever
///         GNU coreutils build happens to be first on <c>PATH</c>, which is not a property a security-relevant read
///         surface should have. Doing the work in managed code removes the dependency on every platform instead of
///         branching on one, so the behaviour a Linux test exercises is byte-for-byte the behaviour Windows runs. That
///         matters more than usual here: this engine has no Windows machine to verify on.
///     </para>
///     <para>
///         <b>The security properties are preserved, and one of them is strengthened.</b> Path confinement still
///         happens in the caller's own path guard before anything reaches this class, and this
///         class additionally refuses a scan root reached through a symbolic link. Symbolic links are never followed
///         and never emitted, which is exactly what <c>find -P … -type f</c> and <c>grep -r</c> did. The suppression
///         rule is now a single predicate supplied by the caller and applied at BOTH the prune step and the emit step,
///         so the generator and the filter can no longer drift — previously the prune expression was built from
///         <see cref="ISensitiveFileExclusionService.SecretEntryNames" /> and
///         the caller's protected-prefix list while the filter re-derived the same
///         decision from <c>IsSecret</c>/<c>IsProtected</c>. Note the caller's predicate must keep gating reads on
///         <see cref="ISensitiveFileExclusionService.IsSecret" /> and not on the broader
///         <see cref="ISensitiveFileExclusionService.IsExcluded" /> copy filter: conflating them would refuse
///         <c>obj/</c>, which an agent legitimately reads after a failed build.
///     </para>
///     <para>
///         <b>Enumeration order is sorted, deliberately.</b> <c>find</c> emitted entries in <c>readdir</c> order, which
///         differs between filesystems (creation order on tmpfs, hash order on ext4). That made the output budget land
///         on different files on different machines and made one truncation defect reproducible only on some hosts.
///         Sorting ordinally by name makes a listing a function of the tree alone.
///     </para>
/// </summary>
internal static class WorkspaceFileScanner
{
    /// <summary>
    ///     Mirrors <c>find -maxdepth 64</c>: the scan root is depth 0, so an entry 64 directories below it is the last
    ///     one visited. It is a runaway guard, not a policy — no real repository approaches it.
    /// </summary>
    public const int MaxDepth = 64;

    /// <summary>
    ///     The most characters of any single line <see cref="SearchText" /> will match against and emit.
    ///     <para>
    ///         A hostile or merely generated repository can hold a single-line file of arbitrary size, and reading such
    ///         a line whole would allocate its full length inside the engine process. The shell-out was bounded by the
    ///         sandbox's own stream cap; managed code has to bound itself. A match beyond 64 KiB into one line is not
    ///         information an agent can act on, so the remainder of an over-long line is skipped rather than buffered —
    ///         and it still counts as one line, so every later line number stays correct.
    ///     </para>
    /// </summary>
    public const int MaxSearchLineChars = 64 * 1024;

    /// <summary>How much of a file is inspected for a NUL byte before deciding it is binary. Mirrors <c>grep -I</c>.</summary>
    private const int BinarySniffBytes = 8 * 1024;

    /// <summary>
    ///     How long a single regular-expression match may run before that line is abandoned. A model-supplied pattern
    ///     can be crafted to backtrack catastrophically, and a survey must not become a way to burn the host's CPU.
    ///     Exceeding it skips the line, never the file and never the survey — the same direction as an unreadable file.
    /// </summary>
    private static readonly TimeSpan RegexLineTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>
    ///     Lists regular files under <paramref name="scanRoot" />, emitting at most <paramref name="maxEntries" />
    ///     workspace-survey paths in the <c>./a/b</c> shape the tools' filters and the model's prompt already expect.
    /// </summary>
    /// <param name="scanRoot">An already-confined absolute host directory inside the managed workspace.</param>
    /// <param name="maxEntries">The emitted-entry ceiling (the caller's listing cap).</param>
    /// <param name="isSuppressed">
    ///     Given a path relative to <paramref name="scanRoot" />, whether it must be neither descended into nor emitted.
    /// </param>
    /// <param name="nameGlob">
    ///     When supplied, only files whose NAME matches this glob are emitted — the shell-out's <c>find -name</c>.
    ///     Matched against the entry name alone, never the path, and never used to prune a directory: a glob is a
    ///     result filter, so pruning on it would hide matching files in non-matching directories.
    /// </param>
    public static List<string> ListFiles(string scanRoot,
        int maxEntries,
        Func<string, bool> isSuppressed,
        string? nameGlob,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scanRoot);
        ArgumentNullException.ThrowIfNull(isSuppressed);

        var glob = string.IsNullOrWhiteSpace(nameGlob) ? null : nameGlob;
        var results = new List<string>();
        Walk(scanRoot,
            isSuppressed,
            (relative, file) =>
            {
                if (glob is not null && !FileSystemName.MatchesSimpleExpression(glob, file.Name, ignoreCase: true))
                {
                    return true;
                }

                results.Add("./" + relative);
                return results.Count < maxEntries;
            },
            cancellationToken);
        return results;
    }

    /// <summary>
    ///     Searches every non-binary regular file under <paramref name="scanRoot" /> and emits
    ///     <c>./path:line:text</c>, stopping at whichever of <paramref name="maxMatches" /> or
    ///     <paramref name="maxOutputBytes" /> is reached first.
    ///     <para>
    ///         <paramref name="isRegex" /> selects between the two modes the shell-out had: fixed-string
    ///         (<c>grep -F</c>, ordinal, the default) and regular expression. A model-supplied expression is compiled
    ///         with a per-line timeout rather than trusted, and an unparseable one is reported to the caller as an
    ///         <see cref="ArgumentException" /> so it can be answered rather than swallowed.
    ///     </para>
    /// </summary>
    public static List<string> SearchText(string scanRoot,
        string pattern,
        bool isRegex,
        int maxMatches,
        int maxOutputBytes,
        Func<string, bool> isSuppressed,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scanRoot);
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(isSuppressed);

        var results = new List<string>();
        if (pattern.Length == 0 || maxMatches <= 0)
        {
            return results;
        }

        var expression = isRegex ? CompilePattern(pattern) : null;
        var emittedBytes = 0;
        Walk(scanRoot,
            isSuppressed,
            (relative, file) => ForEachTextLine(file,
                (lineNumber, text) =>
                {
                    if (!Matches(text, pattern, expression))
                    {
                        return true;
                    }

                    var line = string.Create(CultureInfo.InvariantCulture, $"./{relative}:{lineNumber}:{text}");
                    results.Add(line);
                    emittedBytes += Encoding.UTF8.GetByteCount(line) + 1;
                    return results.Count < maxMatches && emittedBytes < maxOutputBytes;
                },
                cancellationToken),
            cancellationToken);
        return results;
    }

    /// <summary>
    ///     Compiles a model-supplied expression, translating a bad pattern into an <see cref="ArgumentException" /> the
    ///     caller can turn into a model-facing message. Not <c>RegexOptions.Compiled</c>: a survey runs once, and
    ///     compiling a hostile pattern is itself work.
    /// </summary>
    private static Regex CompilePattern(string pattern)
    {
        try
        {
            return new Regex(pattern, RegexOptions.None, RegexLineTimeout);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException("The search pattern is not a valid regular expression.", nameof(pattern), exception);
        }
    }

    private static bool Matches(string text, string pattern, Regex? expression)
    {
        if (expression is null)
        {
            return text.Contains(pattern, StringComparison.Ordinal);
        }

        try
        {
            return expression.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            // One pathological line is not a reason to fail the survey, and reporting it would leak the line.
            return false;
        }
    }

    /// <summary>
    ///     Depth-first, name-sorted walk of regular files. <paramref name="visit" /> returns <see langword="false" /> to
    ///     stop the walk. A directory that cannot be opened is skipped rather than failing the survey — the same thing
    ///     <c>find</c> did with a permission error on one subtree.
    /// </summary>
    private static void Walk(string scanRoot,
        Func<string, bool> isSuppressed,
        Func<string, FileInfo, bool> visit,
        CancellationToken cancellationToken)
    {
        var root = new DirectoryInfo(scanRoot);
        if (!root.Exists)
        {
            throw new DirectoryNotFoundException("The requested Development workspace directory does not exist.");
        }

        EnsureNotReachedThroughLink(root);
        _ = WalkDirectory(root, relativePrefix: string.Empty, depth: 0, isSuppressed, visit, cancellationToken);
    }

    private static bool WalkDirectory(DirectoryInfo directory,
        string relativePrefix,
        int depth,
        Func<string, bool> isSuppressed,
        Func<string, FileInfo, bool> visit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        FileSystemInfo[] entries;
        try
        {
            entries = directory.GetFileSystemInfos();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // An unreadable subtree is skipped, never fatal: one such directory must not fail the whole survey.
            return true;
        }

        Array.Sort(entries, static (left, right) => string.CompareOrdinal(left.Name, right.Name));

        var directories = new List<(DirectoryInfo Directory, string Relative)>();
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Never follow, never emit. A link inside the workspace can name a target outside it, and `find -P` /
            // `grep -r` both declined to follow one — so this is parity, not a new rule.
            if (IsLink(entry))
            {
                continue;
            }

            var relative = relativePrefix.Length == 0 ? entry.Name : relativePrefix + "/" + entry.Name;
            if (isSuppressed(relative))
            {
                continue;
            }

            if (entry is DirectoryInfo child)
            {
                if (depth < MaxDepth)
                {
                    directories.Add((child, relative));
                }

                continue;
            }

            if (entry is FileInfo file && !visit(relative, file))
            {
                return false;
            }
        }

        foreach (var (child, relative) in directories)
        {
            if (!WalkDirectory(child, relative, depth + 1, isSuppressed, visit, cancellationToken))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsLink(FileSystemInfo entry)
    {
        try
        {
            return entry.LinkTarget is not null || entry.Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // An entry whose attributes cannot be read is treated as a link: not following it is the safe direction.
            return true;
        }
    }

    /// <summary>
    ///     Refuses a scan root whose own final component is a symbolic link. The components ABOVE it are the engine's
    ///     own workspace path, already validated when the workspace was created; this closes the one component an agent
    ///     can create, which is the directory it just asked to list.
    /// </summary>
    private static void EnsureNotReachedThroughLink(DirectoryInfo root)
    {
        if (IsLink(root))
        {
            throw new WorkspaceScanRejectedException("The requested workspace directory is a symbolic link.");
        }
    }

    /// <summary>
    ///     Streams a file's lines to <paramref name="visit" />, skipping the file entirely when it looks binary (a NUL
    ///     byte in the first <see cref="BinarySniffBytes" />, which is <c>grep -I</c>'s own heuristic) and bounding any
    ///     single line to <see cref="MaxSearchLineChars" />. Decoding is lenient: an invalid byte sequence becomes a
    ///     replacement character rather than an exception, because a survey must not fail on one badly encoded file.
    ///     <para>
    ///         Returns <see langword="false" /> when <paramref name="visit" /> asked to stop the whole walk. A file that
    ///         cannot be opened or read is skipped, never fatal — one unreadable file must not fail the survey.
    ///     </para>
    /// </summary>
    private static bool ForEachTextLine(FileInfo file, Func<int, string, bool> visit, CancellationToken cancellationToken)
    {
        // The dedicated-local + unconditional-dispose shape rather than `using`: the stream is created inside a try
        // that swallows an open failure, and the reader below leaves it open so ownership stays in exactly one place.
        FileStream? stream = null;
        try
        {
            try
            {
                stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return true;
            }

            if (LooksBinary(stream))
            {
                return true;
            }

            stream.Position = 0;
            using var reader = new StreamReader(stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                detectEncodingFromByteOrderMarks: true,
                bufferSize: -1,
                leaveOpen: true);
            var buffer = new StringBuilder();
            var lineNumber = 0;
            var overlong = false;
            var characters = new char[4096];

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int read;
                try
                {
                    read = reader.Read(characters, 0, characters.Length);
                }
                catch (IOException)
                {
                    return true;
                }

                if (read == 0)
                {
                    break;
                }

                for (var index = 0; index < read; index++)
                {
                    var character = characters[index];
                    if (character == '\n')
                    {
                        lineNumber++;
                        if (!visit(lineNumber, TrimCarriageReturn(buffer)))
                        {
                            return false;
                        }

                        _ = buffer.Clear();
                        overlong = false;
                        continue;
                    }

                    if (overlong)
                    {
                        continue;
                    }

                    if (buffer.Length >= MaxSearchLineChars)
                    {
                        overlong = true;
                        continue;
                    }

                    _ = buffer.Append(character);
                }
            }

            // A file whose last line has no terminator still has that line.
            return (buffer.Length == 0 && !overlong) || visit(lineNumber + 1, TrimCarriageReturn(buffer));
        }
        finally
        {
            stream?.Dispose();
        }
    }

    // A CRLF file must not put a stray CR into the emitted match line: grep strips the record separator, and the CR is
    // part of it on such a repository.
    private static string TrimCarriageReturn(StringBuilder buffer)
    {
        var length = buffer.Length;
        if (length > 0 && buffer[length - 1] == '\r')
        {
            length--;
        }

        return buffer.ToString(0, length);
    }

    private static bool LooksBinary(FileStream stream)
    {
        Span<byte> sniff = stackalloc byte[BinarySniffBytes];
        int read;
        try
        {
            read = stream.ReadAtLeast(sniff, sniff.Length, throwOnEndOfStream: false);
        }
        catch (IOException)
        {
            return true;
        }

        return sniff[..read].Contains((byte)0);
    }
}
