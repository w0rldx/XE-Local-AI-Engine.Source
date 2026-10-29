namespace XE_Local_AI_Engine.Client.Services.Development;

using System.Text;
using System.Text.RegularExpressions;

/// <summary>
///     A deliberately small glob matcher for the profile's protected-path patterns.
/// </summary>
/// <remarks>
///     Hand-rolled rather than taken from <c>Microsoft.Extensions.FileSystemGlobbing</c>, which this assembly does
///     not reference and which would need a Central Package Management entry, and because the matcher takes part in
///     a security decision that must not drift with a transitive package bump. <c>**</c> spans any number of path
///     segments, <c>*</c> matches within one and <c>?</c> matches one non-separator character; matching is
///     case-insensitive, so a rename to <c>featuretests.cs</c> cannot escape the policy.
/// </remarks>
internal static class DevelopmentGlob
{
    private static readonly Dictionary<string, Regex> Cache = [];
    private static readonly Lock CacheGate = new();

    public static bool IsMatch(string pattern, string path)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        Regex? regex;
        lock (CacheGate)
        {
            if (!Cache.TryGetValue(pattern, out regex))
            {
                regex = new Regex(Translate(pattern),
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(250));
                Cache[pattern] = regex;
            }
        }

        return regex.IsMatch(path);
    }

    private static string Translate(string pattern)
    {
        var builder = new StringBuilder("^");
        var index = 0;
        while (index < pattern.Length)
        {
            var current = pattern[index];
            if (current != '*')
            {
                _ = current switch
                {
                    '?' => builder.Append("[^/]"),
                    _ => builder.Append(Regex.Escape(current.ToString()))
                };
                index++;
                continue;
            }

            if (index + 1 < pattern.Length && pattern[index + 1] == '*')
            {
                if (index + 2 < pattern.Length && pattern[index + 2] == '/')
                {
                    // "**/" spans zero or more whole segments, so "**/*Tests.cs" also matches a root-level file.
                    _ = builder.Append("(?:[^/]+/)*");
                    index += 3;
                }
                else
                {
                    _ = builder.Append(".*");
                    index += 2;
                }

                continue;
            }

            _ = builder.Append("[^/]*");
            index++;
        }

        return builder.Append('$').ToString();
    }
}
