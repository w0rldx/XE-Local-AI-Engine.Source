namespace XE_Local_AI_Engine.Client.Services.Agents.Implementation;

using System.IO.Compression;
using System.Text;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>
///     Reads skills out of an untrusted <c>.zip</c> entirely in memory. Nothing is ever written to disk, which removes
///     the symlink-follow and TOCTOU classes outright rather than guarding them, and every remaining guard below fails
///     closed with an operator-visible reason.
/// </summary>
/// <remarks>
///     <para>
///         The bomb guards bound the bytes <em>actually inflated</em>. <see cref="ZipArchiveEntry.Length" /> and
///         <see cref="ZipArchiveEntry.CompressedLength" /> are attacker-authored central-directory fields — assertions,
///         not measurements — so nothing here allocates from them or decides on them. An entry declaring ~4 GiB for a
///         handful of real bytes would otherwise abort a perfectly harmless archive on a single allocation. Each read
///         runs against a buffer one byte longer than the cap: filling it proves the entry is over the limit, which a
///         read that merely reached the cap cannot. (The opposite lie — declare small, inflate huge — is additionally
///         closed by <see cref="ZipArchive" /> itself, whose read path stops inflating at the declared size.)
///     </para>
///     <para>
///         Only entries we intend to keep are inflated at all. A repository full of images and binaries costs nothing
///         and is not refused for content we were never going to store.
///     </para>
/// </remarks>
internal static class SkillArchiveReader
{
    /// <summary>Longest entry path we will even consider, so a pathological name cannot be used as a payload.</summary>
    private const int MaxEntryPathLength = 400;

    private const string SkillFileName = "SKILL.md";

    /// <summary>MAF's own default <c>AllowedResourceExtensions</c>. Anything else is silently ignored, not refused.</summary>
    private static readonly HashSet<string> ResourceExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".md",
            ".json",
            ".yaml",
            ".yml",
            ".csv",
            ".xml",
            ".txt"
        };

    /// <summary>MAF's own default <c>AllowedScriptExtensions</c>. Detected so the report can list them; never imported.</summary>
    private static readonly HashSet<string> ScriptExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".py",
            ".js",
            ".sh",
            ".ps1",
            ".cs",
            ".csx"
        };

    private static readonly Dictionary<string, string> MediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".md"] = "text/markdown",
        [".json"] = "application/json",
        [".yaml"] = "application/yaml",
        [".yml"] = "application/yaml",
        [".csv"] = "text/csv",
        [".xml"] = "application/xml",
        [".txt"] = "text/plain"
    };

    // Encoding.UTF8 substitutes U+FFFD for invalid bytes, which would turn this guard into a no-op that silently
    // corrupts content instead of refusing it. throwOnInvalidBytes is the whole point of validating here.
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    ///     Discovers every skill folder in <paramref name="archive" />. A skill folder is any directory holding a
    ///     <c>SKILL.md</c>; the layout root is never hard-coded, because the real-world layouts differ (a bare skill
    ///     folder, a GitHub archive's version-stamped top directory, or skills nested several levels down under
    ///     <c>.github/plugins/</c>).
    /// </summary>
    /// <exception cref="SkillImportException">Any guard tripped.</exception>
    public static IReadOnlyList<SkillArchiveFolder> Read(ReadOnlyMemory<byte> archive, SkillImportOptions options)
    {
        if (archive.Length > options.MaxArchiveBytes)
        {
            throw new SkillImportException($"The archive is larger than the {options.MaxArchiveBytes / (1024 * 1024)} MiB import limit.");
        }

        using var stream = new MemoryStream(archive.ToArray(), writable: false);
        using var zip = OpenArchive(stream);

        var entries = InspectEntries(zip, options);
        var roots = entries.Keys
                           .Where(static path => IsSkillFile(path))
                           .Select(static path => path[..^SkillFileName.Length])
                           .ToList();

        if (roots.Count == 0)
        {
            throw new SkillImportException("No SKILL.md was found in the archive.");
        }

        var budget = new InflationBudget(options.MaxTotalInflatedBytes);
        return roots.Select(root => ReadFolder(entries, root, budget, options))
                    .OrderBy(static folder => folder.RootPath, StringComparer.Ordinal)
                    .ToList();
    }

    private static ZipArchive OpenArchive(Stream stream)
    {
        try
        {
            return new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException exception)
        {
            throw new SkillImportException("The upload is not a readable .zip archive.", exception);
        }
    }

    /// <summary>
    ///     Walks the central directory once, applying every guard that is decidable from an entry's header, and returns
    ///     the surviving file entries keyed by their (forward-slash) path.
    /// </summary>
    private static Dictionary<string, ZipArchiveEntry> InspectEntries(ZipArchive zip, SkillImportOptions options)
    {
        var entries = zip.Entries;
        if (entries.Count > options.MaxEntries)
        {
            throw new SkillImportException($"The archive holds more than {options.MaxEntries} entries.");
        }

        // Ordinal, not case-insensitive: the divergence being closed is that ZipArchive.Entries yields BOTH entries
        // with the same FullName while GetEntry returns only the first, so a duplicate lets the preview and the persist
        // disagree about what was approved. Case-differing names are genuinely distinct entries on the wire.
        var result = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var path = entry.FullName.Replace(oldChar: '\\', newChar: '/');

            if (IsSymbolicLink(entry))
            {
                // Refused as an entry — never resolved, never a skill root, never a resource. Its payload is a path
                // string, so following it would re-introduce the escape class that keeping extraction in memory
                // removed. Dropping the entry rather than the whole archive is deliberate and load-bearing: the
                // published collection repositories DO ship symlinked skill folders whose targets are real
                // directories in the same archive, so scanning for SKILL.md still finds every skill, and aborting
                // would make the flagship source unimportable for no security gain.
                continue;
            }

            if (path.EndsWith('/'))
            {
                continue;
            }

            if (!IsSafeEntryPath(path))
            {
                throw new SkillImportException("The archive contains an entry whose path is unsafe (absolute, traversing, or containing control characters).");
            }

            if (!result.TryAdd(path, entry))
            {
                throw new SkillImportException("The archive contains two entries with the same path.");
            }
        }

        return result;
    }

    private static SkillArchiveFolder ReadFolder(Dictionary<string, ZipArchiveEntry> entries,
        string root,
        InflationBudget budget,
        SkillImportOptions options)
    {
        var files = new List<SkillArchiveFile>();
        var refusedScripts = new List<string>();
        var resourceLimitExceeded = false;

        foreach (var (path, entry) in entries.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            if (!path.StartsWith(root, StringComparison.Ordinal) || IsSkillFile(path))
            {
                continue;
            }

            // A file belongs to the deepest skill folder that contains it, so a nested skill's resources — and its
            // refused scripts — are not also claimed by an ancestor folder.
            if (OwnedByDeeperRoot(entries, root, path))
            {
                continue;
            }

            var relative = path[root.Length..];

            if (IsScript(relative))
            {
                refusedScripts.Add(relative);
                continue;
            }

            if (!ResourceExtensions.Contains(Path.GetExtension(relative)))
            {
                continue;
            }

            // Noted and skipped rather than inflated: the excess is refused, so there is no reason to pay for it.
            if (files.Count >= options.MaxResourcesPerSkill)
            {
                resourceLimitExceeded = true;
                continue;
            }

            files.Add(new SkillArchiveFile(relative, MediaTypeFor(relative), ReadText(entry, budget, options)));
        }

        var directoryName = root.Length == 0 ? string.Empty : root.TrimEnd('/').Split('/')[^1];
        return new SkillArchiveFolder(directoryName,
            root,
            ReadText(entries[root + SkillFileName], budget, options),
            files,
            refusedScripts,
            resourceLimitExceeded);
    }

    private static bool OwnedByDeeperRoot(Dictionary<string, ZipArchiveEntry> entries, string root, string path)
    {
        // Cheap because the candidate set is only the ancestors of this path below the current root.
        var separator = path.LastIndexOf('/');
        while (separator > root.Length)
        {
            if (entries.ContainsKey(path[..(separator + 1)] + SkillFileName))
            {
                return true;
            }

            separator = path.LastIndexOf(value: '/', separator - 1);
        }

        return false;
    }

    /// <summary>
    ///     Inflates one entry under the per-entry, whole-archive and ratio caps, then validates it as strict UTF-8.
    /// </summary>
    private static string ReadText(ZipArchiveEntry entry, InflationBudget budget, SkillImportOptions options)
    {
        var bytes = ReadBounded(entry, options);

        // CompressedLength is attacker-authored too, but understating it can only make the ratio look worse, so this
        // comparison can over-reject and never under-reject. A zero compressed length with real output is a lie.
        if (entry.CompressedLength <= 0 ? bytes.Length > 0 : bytes.Length / entry.CompressedLength > options.MaxCompressionRatio)
        {
            throw new SkillImportException($"The archive contains an entry that inflates more than {options.MaxCompressionRatio}:1.");
        }

        budget.Add(bytes.Length);

        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new SkillImportException("The archive contains a file that is not valid UTF-8 text.", exception);
        }

        if (text.Contains('\0', StringComparison.Ordinal))
        {
            throw new SkillImportException("The archive contains a file with an embedded NUL byte.");
        }

        return text.TrimStart('\uFEFF');
    }

    private static byte[] ReadBounded(ZipArchiveEntry entry, SkillImportOptions options)
    {
        using var source = entry.Open();

        // The cap is measured against bytes ACTUALLY INFLATED; entry.Length is never read. Both directions of the
        // header lie are covered, and only one of them is reachable — keep it that way:
        //   over-declaring  (say 4 GiB, ship 20 bytes)  IS reachable: sizing a buffer from Length OOMs on a harmless
        //                                               archive. Immunity comes from never reading Length.
        //   under-declaring (say 4 KiB, ship 2 MiB)     is NOT constructible via ZipArchive: its read path stops
        //                                               inflating at the declared size (measured: yields exactly 4096).
        // A future reader may be tempted to "harden" by comparing Length against the cap. That checks the unreachable
        // case, re-introduces the reachable one, and hardens nothing.
        // One byte longer than the cap, so "filled the buffer" means over-limit rather than exactly-at-limit.
        var buffer = new byte[options.MaxEntryBytes + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = source.Read(buffer, total, buffer.Length - total);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        if (total > options.MaxEntryBytes)
        {
            throw new SkillImportException($"The archive contains a file larger than the {options.MaxEntryBytes / 1024} KiB per-file limit.");
        }

        return buffer[..total];
    }

    /// <summary>
    ///     Unix mode lives in the high 16 bits of the external attributes; <c>0xA000</c> is <c>S_IFLNK</c>. A
    ///     Windows-authored archive leaves those bits clear, so this cannot false-positive on DOS attributes.
    /// </summary>
    private static bool IsSymbolicLink(ZipArchiveEntry entry)
    {
        return ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000;
    }

    /// <summary>
    ///     <see cref="GgufFilePath.IsSafeRelativePath" /> contributes the already-hardened rooted and <c>..</c>/<c>.</c>
    ///     segment checks. It does not cover control characters or device/drive prefixes — and on Linux
    ///     <c>Path.IsPathRooted(@"\\?\C:\x")</c> is false — so the rest is checked here.
    /// </summary>
    private static bool IsSafeEntryPath(string path)
    {
        return path.Length <= MaxEntryPathLength
               && GgufFilePath.IsSafeRelativePath(path)
               && !path.Contains('\\', StringComparison.Ordinal)
               && !path.Contains(':', StringComparison.Ordinal)
               && !path.Any(char.IsControl);
    }

    private static bool IsSkillFile(string path)
    {
        return path.EndsWith(SkillFileName, StringComparison.Ordinal)
               && (path.Length == SkillFileName.Length || path[path.Length - SkillFileName.Length - 1] == '/');
    }

    /// <summary>Extension match, plus anything under a <c>scripts/</c> directory — MAF's own script-location default.</summary>
    private static bool IsScript(string relativePath)
    {
        return ScriptExtensions.Contains(Path.GetExtension(relativePath))
               || relativePath.Split('/').SkipLast(count: 1).Any(static segment => segment.Equals("scripts", StringComparison.OrdinalIgnoreCase));
    }

    private static string MediaTypeFor(string relativePath)
    {
        return MediaTypes.GetValueOrDefault(Path.GetExtension(relativePath), "text/plain");
    }

    /// <summary>Running total of inflated bytes across the whole archive, so the caps compose instead of being per-entry only.</summary>
    private sealed class InflationBudget(int maxTotalBytes)
    {
        private long _total;

        public void Add(int bytes)
        {
            _total += bytes;
            if (_total > maxTotalBytes)
            {
                throw new SkillImportException($"The archive inflates to more than the {maxTotalBytes / (1024 * 1024)} MiB import limit.");
            }
        }
    }
}

/// <summary>One discovered skill folder: its <c>SKILL.md</c>, the bundled files kept, and the scripts refused.</summary>
/// <param name="DirectoryName">Last segment of <paramref name="RootPath" />; empty when <c>SKILL.md</c> sits at the archive root.</param>
/// <param name="RootPath">Skill-root prefix inside the archive, with a trailing slash (empty at the archive root).</param>
/// <param name="ResourceLimitExceeded">The folder carried more bundled files than the per-skill cap; the excess was never inflated.</param>
internal sealed record SkillArchiveFolder(
    string DirectoryName,
    string RootPath,
    string SkillMarkdown,
    IReadOnlyList<SkillArchiveFile> Files,
    IReadOnlyList<string> RefusedScripts,
    bool ResourceLimitExceeded = false);

/// <summary>One bundled file, named relative to its skill root — the path the model looks it up by.</summary>
internal sealed record SkillArchiveFile(string Name, string MediaType, string Content);
