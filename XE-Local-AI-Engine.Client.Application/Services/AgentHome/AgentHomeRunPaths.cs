namespace XE_Local_AI_Engine.Client.Services.AgentHome;

using System.Globalization;
using XE_Local_AI_Engine.Providers.Abstractions;

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

    /// <summary>
    ///     The one gate every caller that reaches into a named run directory passes, or <see langword="null" /> when
    ///     the name is not a run this node can serve.
    /// </summary>
    /// <remarks>
    ///     Each check only means something after the one before it: an unminted name is unknowable, so it is never
    ///     touched (it also cannot carry a separator, a <c>..</c> or a root, which keeps
    ///     <see cref="Path.Combine(string, string)" /> from composing anything but a child); lexical containment is
    ///     the belt on that; the link check keeps a planted link from being treated as the tree it points at. A run
    ///     that does not exist reads the same as one refused, so a probe learns nothing from the difference.
    /// </remarks>
    public static AgentHomeRunLocation? TryResolveRun(string runsRoot, string runDirectoryName)
    {
        if (TryParseStartedAt(runDirectoryName) is not { } startedAt)
        {
            return null;
        }

        var path = Path.Combine(runsRoot, runDirectoryName);
        return Directory.Exists(path) && PathContainment.IsUnderRoot(path, runsRoot) && !IsLink(path)
            ? new AgentHomeRunLocation(path, runDirectoryName, startedAt)
            : null;
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
            // The walk is inside the try, not just the call that starts it: enumeration is lazy, so a directory
            // deleted mid-sweep throws from the iterator, and letting that out would abort the whole sweep tick.
            try
            {
                foreach (var entry in new DirectoryInfo(pending.Pop()).EnumerateFileSystemInfos())
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
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // An unreadable or vanished directory contributes what was measured before it went.
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
