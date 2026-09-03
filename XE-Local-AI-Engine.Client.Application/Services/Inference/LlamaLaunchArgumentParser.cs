namespace XE_Local_AI_Engine.Client.Services.Inference;

using System.Text;

/// <summary>
///     Parses an operator-entered raw <c>llama-server</c> extra-argument string into a token list and enforces the
///     safety rule the per-model launch-args override carries: the operator may override the bundled sampling/decoding
///     tuning flags freely (that IS the experiment), but NOT the flags the app manages. Two families are managed and are
///     rejected on write / stripped on read:
///     <list type="bullet">
///         <item>
///             <b>Reachability</b> — the model path (<c>-m</c>/<c>--model</c>), the loopback bind (<c>--host</c>), and
///             the allocated port (<c>--port</c>). Overriding any of these breaks the app's ability to reach the process
///             it launched.
///         </item>
///         <item>
///             <b>Memory-fit placement</b> — the context size, GPU-layer/tensor placement, KV-cache type,
///             flash-attention, parallel slots, and batch sizes (<c>-c</c>, <c>-ngl</c>, <c>-ts</c>, <c>-ot</c>,
///             <c>--cpu-moe</c>/<c>--n-cpu-moe</c>, <c>-ctk</c>/<c>-ctv</c>, <c>-fa</c>, <c>--parallel</c>,
///             <c>-b</c>/<c>-ub</c> and their long aliases). The
///             capacity/allocation resolver and the launch policy decide these BEFORE admission and record the resulting
///             footprint in the memory ledger. The override is appended to the spec AFTER that decision, so letting it
///             change a placement flag would silently invalidate the ledger, defeat the safe-config retry, and
///             overcommit RAM/VRAM. Re-exploring/re-tuning is the supported way to change placement.
///         </item>
///     </list>
///     Everything else llama.cpp supports (sampling, RoPE, penalties, samplers, grammar, mirostat, …) stays available.
/// </summary>
/// <remarks>
///     Tokenizing is a small quote-aware split (single or double quotes group a token; whitespace outside quotes
///     separates), enough to pass values such as <c>--samplers "top_k;top_p"</c> through as one token. It is deliberately
///     not a full shell parser — this is a developer experimentation knob, not a command interpreter.
/// </remarks>
public static class LlamaLaunchArgumentParser
{
    // Flags the app manages, so an operator override of any of them is rejected (write) and stripped (read). Two
    // families (see the class doc): reachability (model path / host / port) and memory-fit placement (context,
    // GPU-layer/tensor placement, KV type, flash-attention, parallel slots, batch). Ordinal — llama.cpp flags are ASCII.
    private static readonly string[] ReservedFlags =
    [
        // Reachability — the app binds these to reach the process it launched.
        "-m", "--model", "--host", "--port",

        // Memory-fit placement — owned by the capacity/allocation resolver + launch policy BEFORE admission; a post-hoc
        // override would invalidate the memory ledger, defeat the safe-config retry, and overcommit RAM/VRAM.
        "-c", "--ctx-size",
        "-ngl", "--gpu-layers", "--n-gpu-layers",
        "-ts", "--tensor-split",
        "-ot", "--override-tensor",
        // --cpu-moe/-cmoe and --n-cpu-moe/-ncmoe are -ot by another name: upstream pushes them into the SAME
        // tensor_buft_overrides list the -ot flag writes (llama.cpp common/arg.cpp), so an override could re-place
        // every expert tensor after the placement verdict admission already booked a footprint for.
        "-cmoe", "--cpu-moe",
        "-ncmoe", "--n-cpu-moe",
        "-ctk", "--cache-type-k",
        "-ctv", "--cache-type-v",
        "-fa", "--flash-attn",
        "-np", "--parallel",
        "-b", "--batch-size",
        "-ub", "--ubatch-size",

        // Adapter identity — the registry decides whether a model launches with an adapter and which one, and the
        // launch-policy fingerprint commits to that choice. An operator-supplied --lora would load weights the
        // fingerprint, the memory ledger, and the model's registered identity all know nothing about.
        "--lora", "--lora-scaled"
    ];

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
