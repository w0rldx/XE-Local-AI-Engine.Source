namespace XE_Local_AI_Engine.Client.Services.AgentHome;

using System.Globalization;

/// <summary>
///     Where the agent-home layout and a run's artifacts live, and how a run directory name maps back to the
///     instant it was minted. The only place either root is derived: a second spelling of <c>agent-home</c> or
///     <c>runs</c> is a divergence nothing catches.
/// </summary>
internal static class AgentHomeRunPaths
{
    /// <summary>The directory the layout root always ends with — <c>WipeAgentHome</c>'s guard reads this name.</summary>
    public const string AgentHomeDirectoryName = "agent-home";

    /// <summary>The runs directory inside the agent-home root.</summary>
    public const string RunsDirectoryName = "runs";

    private const string DefaultRootDirectoryName = "agent-home-state";

    /// <summary>The prefix <c>AgentHomeService.CreateRunId</c> stamps on every run id.</summary>
    private const string RunIdPrefix = "run-";

    /// <summary>The absolute <c>&lt;root&gt;/agent-home</c> layout root for the configured options.</summary>
    public static string ResolveAgentHomeRoot(AgentHomeOptions options, string dataDirectoryRoot)
    {
        ArgumentNullException.ThrowIfNull(options);
        var baseRoot = string.IsNullOrWhiteSpace(options.RootPath)
            ? Path.Combine(dataDirectoryRoot, DefaultRootDirectoryName)
            : options.RootPath;
        return Path.Combine(baseRoot, AgentHomeDirectoryName);
    }

    /// <summary>The absolute <c>&lt;root&gt;/agent-home/runs</c> directory for the configured options.</summary>
    public static string ResolveRunsRoot(AgentHomeOptions options, string dataDirectoryRoot)
    {
        return Path.Combine(ResolveAgentHomeRoot(options, dataDirectoryRoot), RunsDirectoryName);
    }

    /// <summary>
    ///     The instant encoded in a <c>run-{unixMs}-{counter}</c> directory name, or <see langword="null" /> when the
    ///     name is not one the node minted.
    /// </summary>
    /// <remarks>
    ///     The id is the age source rather than any filesystem timestamp because the node writes it once and nothing
    ///     afterwards moves it: a copy, a backup restore or a <c>touch</c> all rewrite directory and file times, and
    ///     each would either resurrect an expired run or age a live one into a sweep. A name that does not parse is
    ///     unknown, and an unknown run is never deleted.
    /// </remarks>
    public static DateTimeOffset? TryParseStartedAt(string directoryName)
    {
        if (string.IsNullOrEmpty(directoryName) || !directoryName.StartsWith(RunIdPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var rest = directoryName.AsSpan(RunIdPrefix.Length);
        var separator = rest.IndexOf('-');
        if (separator <= 0 || separator == rest.Length - 1)
        {
            return null;
        }

        var unixMilliseconds = rest[..separator];
        var counter = rest[(separator + 1)..];
        if (!IsAsciiDigits(unixMilliseconds) || !IsAsciiDigits(counter)
            || !long.TryParse(unixMilliseconds, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(parsed);
        }
        catch (ArgumentOutOfRangeException)
        {
            // A digit string long enough to overflow the epoch range is not a run this node minted.
            return null;
        }
    }

    /// <summary>Bytes a run occupies, walking its own tree only — a link is counted as itself, never followed.</summary>
    /// <remarks>
    ///     Its own walk rather than <c>EnumerateFiles(SearchOption.AllDirectories)</c>, which recurses THROUGH a
    ///     directory link and would let a run's log directory report the size of whatever a planted link points at.
    ///     The entry ceiling stops a pathological tree from stalling a sweep or a page load.
    /// </remarks>
    public static long MeasureBytes(string runDirectory, int maxEntries)
    {
        var total = 0L;
        var visited = 0;
        var pending = new Stack<string>();
        pending.Push(runDirectory);

        while (pending.Count > 0 && visited < maxEntries)
        {
            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(pending.Pop()).EnumerateFileSystemInfos();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if (++visited >= maxEntries)
                {
                    break;
                }

                if (entry.LinkTarget is not null)
                {
                    continue;
                }

                if (entry is DirectoryInfo)
                {
                    pending.Push(entry.FullName);
                    continue;
                }

                try
                {
                    total += ((FileInfo)entry).Length;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // A file that vanished between the walk and the stat contributes nothing.
                }
            }
        }

        return total;
    }

    /// <summary>Whether this directory entry is a symbolic link or reparse point. Unreadable reads as "yes".</summary>
    public static bool IsLink(string path)
    {
        try
        {
            return new DirectoryInfo(path).LinkTarget is not null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Unreadable means unclassifiable, which means untouchable.
            return true;
        }
    }

    private static bool IsAsciiDigits(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (!char.IsAsciiDigit(character))
            {
                return false;
            }
        }

        return !value.IsEmpty;
    }
}
