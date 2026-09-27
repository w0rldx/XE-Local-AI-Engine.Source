namespace XE_Local_AI_Engine.Client.Services.Inference;

using System.Text;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Parses an operator-entered raw <c>llama-server</c> extra-argument string and enforces the override's safety
///     rule: the operator may freely override the bundled sampling/decoding flags (that IS the experiment), but NOT
///     the flags the app manages.
/// </summary>
/// <remarks>
///     The managed flags (<see cref="LlamaServerManagedFlags" />, owned by the provider beside the composer that emits
///     them) are rejected on write and stripped on read, while everything else llama.cpp supports stays available; the
///     memory-ledger rationale is in docs/wiki/03-local-runtime-and-providers.md ("2.7 Per-model extra launch
///     arguments (operator override)"). Tokenizing is a small quote-aware split, enough to pass a value such as
///     <c>--samplers "top_k;top_p"</c> as one token: a developer experimentation knob, not a shell parser.
/// </remarks>
public static class LlamaLaunchArgumentParser
{
    /// <summary>Splits <paramref name="raw" /> into tokens, honoring single/double quotes. Null/blank yields an empty list.</summary>
    public static IReadOnlyList<string> Tokenize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        var tokens = new List<string>();
        var current = new StringBuilder();
        var inToken = false;
        var quote = '\0';

        foreach (var ch in raw)
        {
            if (quote != '\0')
            {
                if (ch == quote)
                {
                    quote = '\0';
                }
                else
                {
                    _ = current.Append(ch);
                }

                continue;
            }

            if (ch is '"' or '\'')
            {
                quote = ch;
                inToken = true;
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                if (inToken)
                {
                    tokens.Add(current.ToString());
                    _ = current.Clear();
                    inToken = false;
                }

                continue;
            }

            _ = current.Append(ch);
            inToken = true;
        }

        if (inToken)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    /// <summary>
    ///     Returns the first reserved flag present in <paramref name="raw" /> (matching either <c>--host</c> or
    ///     <c>--host=…</c> forms), or <c>null</c> when none is present. Used by the write path to reject with a clear
    ///     message naming the offending flag.
    /// </summary>
    public static string? FindReservedFlag(string? raw)
    {
        return Tokenize(raw)
               .Select(MatchReserved)
               .FirstOrDefault(reserved => reserved is not null);
    }

    /// <summary>
    ///     Tokenizes <paramref name="raw" /> and drops any reserved flag (and its immediately following value token when
    ///     the value is space-separated rather than <c>=</c>-joined). The safe token list the spawn path appends to the
    ///     launch spec. Never throws.
    /// </summary>
    public static IReadOnlyList<string> ParseSanitized(string? raw)
    {
        var tokens = Tokenize(raw);
        if (tokens.Count == 0)
        {
            return tokens;
        }

        // A while loop (not for) so the index can advance by two when a bare reserved flag consumes its value token,
        // without the analyzer flagging a mutated for-counter.
        var result = new List<string>(tokens.Count);
        var index = 0;
        while (index < tokens.Count)
        {
            var token = tokens[index];
            var reserved = MatchReserved(token);
            if (reserved is null)
            {
                result.Add(token);
                index++;
                continue;
            }

            // Drop a space-separated value that follows a bare reserved flag (e.g. `--host 0.0.0.0`); a `--host=…` token
            // carries its own value. A following token that is itself a flag (starts with '-') is NOT consumed.
            var isBareFlagWithValue = string.Equals(token, reserved, StringComparison.Ordinal)
                                      && index + 1 < tokens.Count
                                      && !tokens[index + 1].StartsWith('-');
            index += isBareFlagWithValue ? 2 : 1;
        }

        return result;
    }

    private static string? MatchReserved(string token)
    {
        return LlamaServerManagedFlags.All.FirstOrDefault(reserved =>
            string.Equals(token, reserved, StringComparison.Ordinal)
            || token.StartsWith(reserved + "=", StringComparison.Ordinal));
    }
}
