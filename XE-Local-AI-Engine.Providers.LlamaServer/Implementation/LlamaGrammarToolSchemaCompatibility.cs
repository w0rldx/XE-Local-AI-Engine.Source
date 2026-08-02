namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.Buffers;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

/// <summary>
///     A llama.cpp-only compatibility pass over a tool's parameter JSON schema: it removes the schema keywords whose
///     bounds llama.cpp's <c>json-schema-to-grammar</c> converter cannot compile into GBNF, so offering a tool can never
///     fail sampler initialisation.
///     <para>
///         llama-server compiles the whole <c>tools</c> array into one constrained grammar up front (for any chat
///         template without a reasoning preamble — a reasoning model simply never enters the constrained branch, which is
///         why this looks model-specific and is not). A repetition bound that is too large is rejected outright:
///         <c>parse: error parsing grammar: number of repetitions exceeds sane defaults</c>, surfaced to us as
///         HTTP 400 <c>Failed to initialize samplers: failed to parse grammar</c> — the request never reaches inference.
///     </para>
///     <para>
///         The schema is advisory to the model in this codebase; authoritative argument validation lives in each tool
///         handler and in <c>ToolArgumentRepairAIFunction</c>, which validates against the UNSANITISED schema several
///         layers above this one. Dropping a bound here therefore narrows nothing — it only stops llama.cpp from
///         refusing to build a grammar. Non-llama.cpp providers (Codex, Azure Foundry) never run this pass and keep
///         receiving the full schema.
///     </para>
/// </summary>
internal static partial class LlamaGrammarToolSchemaCompatibility
{
    /// <summary>
    ///     The largest repetition bound a tool schema may carry on the llama.cpp wire.
    ///     <para>
    ///         Measured against a real llama-server (source-build CUDA b10201) one keyword per request: <c>maxLength</c>
    ///         1990 compiles, 2000 fails. But the converter's budget is also spent ACROSS the whole <c>tools</c> array
    ///         (a second error variant: "number of rules that are going to be repeated multiplied by the new repetition
    ///         exceeds sane defaults"), so a per-keyword threshold just under the per-keyword cliff is not enough: the
    ///         real ten-tool offer still FAILS with every bound clamped to 2048 and COMPILES at 1024. 1024 is therefore
    ///         the largest power-of-two bound proven to compile for the full production offer, with roughly 2x headroom
    ///         under the measured per-keyword cliff for the offer to grow into.
    ///     </para>
    ///     <para>
    ///         Integer <c>minimum</c>/<c>maximum</c> are deliberately NOT bounded here — they are value ranges, not
    ///         repetition counts, and were measured safe at 100000.
    ///     </para>
    /// </summary>
    internal const int MaxGrammarRepetitionBound = 1024;

    /// <summary>
    ///     Returns <paramref name="schema" /> itself when every bound it carries is already compilable, and otherwise a
    ///     new element with the offending keywords removed. Returning the input instance keeps the common path (every
    ///     already-safe tool, every request) free of any parse/rewrite allocation.
    /// </summary>
    internal static JsonElement Sanitize(JsonElement schema)
    {
        if (!RequiresSanitizing(schema))
        {
            return schema;
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteSanitized(schema, writer);
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);

        // Detached clone: the temporary document is disposed on return, so the element must not stay bound to it.
        return document.RootElement.Clone();
    }

    /// <summary>
    ///     True when <paramref name="schema" /> carries at least one bound above <see cref="MaxGrammarRepetitionBound" />
    ///     anywhere in its tree. This is the allocation-free pre-check <see cref="Sanitize" /> and the chat client's
    ///     per-tool scan both run first.
    /// </summary>
    internal static bool RequiresSanitizing(JsonElement schema)
    {
        return schema.ValueKind switch
        {
            JsonValueKind.Object => schema.EnumerateObject().Any(static property => ExceedsBound(property) || RequiresSanitizing(property.Value)),
            JsonValueKind.Array => schema.EnumerateArray().Any(static item => RequiresSanitizing(item)),
            _ => false
        };
    }

    // Matches a regex repetition quantifier — {n}, {n,m} or {n,} — that is not itself escaped. The digit runs are
    // deliberately unbounded: capping them would make an absurdly long bound fail to match at all and slip through.
    [GeneratedRegex(@"(?<!\\)\{\s*(?<min>\d+)\s*(?:,\s*(?<max>\d+)?\s*)?\}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex RepetitionQuantifierPattern();

    // Only these four keywords drive GBNF repetition unrolling, plus `pattern`, whose own regex quantifiers are unrolled
    // the same way. Everything else in a JSON schema is passed through untouched.
    private static bool ExceedsBound(JsonProperty property)
    {
        if (property.NameEquals("pattern"))
        {
            return property.Value.ValueKind == JsonValueKind.String && PatternExceedsBound(property.Value.GetString());
        }

        if (!property.NameEquals("maxLength")
            && !property.NameEquals("minLength")
            && !property.NameEquals("maxItems")
            && !property.NameEquals("minItems"))
        {
            return false;
        }

        return property.Value.ValueKind == JsonValueKind.Number
               && property.Value.TryGetDouble(out var bound)
               && bound > MaxGrammarRepetitionBound;
    }

    // A `pattern` keyword is dropped WHOLE (it has no partial form), and only when one of its quantifiers would unroll
    // past the cap. `{n}` costs n repetitions, `{n,m}` costs m, and `{n,}` costs n plus an unbounded tail that llama.cpp
    // emits as a recursive rule rather than by unrolling — so the open form is judged on its lower bound.
    private static bool PatternExceedsBound(string? pattern)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return false;
        }

        try
        {
            return RepetitionQuantifierPattern().Matches(pattern).Any(static match => QuantifierRepetitions(match) > MaxGrammarRepetitionBound);
        }
        catch (RegexMatchTimeoutException)
        {
            // Fail closed: an un-scannable pattern is dropped rather than risking a grammar the server refuses to build.
            return true;
        }
    }

    // The repetition count one matched quantifier costs: the upper bound when one is written (`{n,m}`), otherwise the
    // lower bound (`{n}`, `{n,}`). A bound too large to parse as an Int32 is, by definition, past the cap.
    private static int QuantifierRepetitions(Match match)
    {
        var written = match.Groups["max"];
        var bound = written.Success
            ? written.ValueSpan
            : match.Groups["min"].ValueSpan;

        return int.TryParse(bound, NumberStyles.None, CultureInfo.InvariantCulture, out var repetitions)
            ? repetitions
            : int.MaxValue;
    }

    private static void WriteSanitized(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    if (ExceedsBound(property))
                    {
                        continue;
                    }

                    writer.WritePropertyName(property.Name);
                    WriteSanitized(property.Value, writer);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteSanitized(item, writer);
                }

                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}

/// <summary>
///     A <see cref="DelegatingAIFunction" /> that swaps ONLY the model-visible <see cref="AIFunction.JsonSchema" /> for
///     its llama.cpp-compilable form, forwarding name, description, additional properties and invocation to the inner
///     function (via <see cref="DelegatingAIFunction" />).
///     <para>
///         It exists solely so the OpenAI adapter serialises a compilable <c>tools</c> array. It is created inside
///         <c>DeferredLlamaServerChatClient</c> on a CLONE of the caller's <see cref="ChatOptions" />, so it is never
///         visible to the layers that resolve and execute tools: the function-invocation middleware, its approval
///         detection, and <c>ApprovalRequiredAIFunction</c>'s outermost-type contract all operate on the caller's own
///         (untouched) tool list. Consequently this wrapper is never invoked and never participates in argument
///         validation.
///     </para>
/// </summary>
internal sealed class GrammarSafeSchemaAIFunction : DelegatingAIFunction
{
    private readonly JsonElement _jsonSchema;

    internal GrammarSafeSchemaAIFunction(AIFunction innerFunction, JsonElement jsonSchema)
        : base(innerFunction)
    {
        _jsonSchema = jsonSchema;
    }

    public override JsonElement JsonSchema => _jsonSchema;
}
