namespace XE_Local_AI_Engine.Client.Services.Inference;

/// <summary>
///     Parses an operator-entered raw <c>llama-server</c> extra-argument string into a token list and enforces the one
///     safety rule the per-model launch-args override carries: the operator may override the bundled launch policy's
///     tuning flags freely (that IS the experiment), but NOT the few flags wired to the supervisor's process contract —
///     the model path (<c>-m</c>/<c>--model</c>), the loopback bind (<c>--host</c>), and the allocated port
///     (<c>--port</c>). Changing any of those would break the app's ability to reach the process it launched, so they are
///     rejected on write and defensively stripped on read.
/// </summary>
/// <remarks>
///     Tokenizing is a small quote-aware split (single or double quotes group a token; whitespace outside quotes
///     separates), enough to pass values such as <c>-ot "\.ffn.*=CPU"</c> through as one token. It is deliberately not a
///     full shell parser — this is a developer experimentation knob, not a command interpreter.
/// </remarks>
public static class LlamaLaunchArgumentParser
{
    // Flags the supervisor owns because they are bound to the launched process's identity/reachability. An operator
    // override of any of these is rejected (write) and stripped (read). Ordinal — llama.cpp flags are ASCII.
    private static readonly string[] ReservedFlags = ["-m", "--model", "--host", "--port"];

    /// <summary>Splits <paramref name="raw" /> into tokens, honoring single/double quotes. Null/blank yields an empty list.</summary>
    public static IReadOnlyList<string> Tokenize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
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

            // Drop a space-separated value that follows a bare reserved flag (e.g. `--host 0.0.0.0`). A `--host=…`
            // token carries its own value, so nothing extra is consumed. A following token that is itself a flag
            // (starts with '-') is NOT consumed — the reserved flag had no value.
            var isBareFlagWithValue = string.Equals(token, reserved, StringComparison.Ordinal)
                                      && index + 1 < tokens.Count
                                      && !tokens[index + 1].StartsWith('-');
            index += isBareFlagWithValue ? 2 : 1;
        }

        return result;
    }

    private static string? MatchReserved(string token)
    {
        return Array.Find(ReservedFlags,
            reserved => string.Equals(token, reserved, StringComparison.Ordinal)
                        || token.StartsWith(reserved + "=", StringComparison.Ordinal));
    }
}
