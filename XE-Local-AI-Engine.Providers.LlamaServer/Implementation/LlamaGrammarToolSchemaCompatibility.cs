namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.Buffers;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

/// <summary>
///     A llama.cpp-only pass over a tool's parameter JSON schema: it removes the keywords whose bounds llama.cpp's
///     <c>json-schema-to-grammar</c> converter cannot compile into GBNF, so offering a tool never fails sampler setup.
/// </summary>
/// <remarks>
///     Dropping a bound here narrows nothing: the schema is advisory to the model, and authoritative argument
///     validation lives in each tool handler and in <c>ToolArgumentRepairAIFunction</c>, which validates against the
///     UNSANITISED schema several layers above this one. Non-llama.cpp providers (Codex, Azure Foundry) never run this
///     pass and keep receiving the full schema. What llama-server does with the <c>tools</c> array and how it fails:
///     docs/wiki/03-local-runtime-and-providers.md, "The grammar repetition bound".
/// </remarks>
internal static partial class LlamaGrammarToolSchemaCompatibility
{
    /// <summary>
    ///     The largest repetition bound a tool schema may carry on the llama.cpp wire: the largest power-of-two bound
    ///     proven to compile for the full production tool offer.
    /// </summary>
    /// <remarks>
    ///     Integer <c>minimum</c>/<c>maximum</c> are deliberately NOT bounded here — they are value ranges, not
    ///     repetition counts, and were measured safe at 100000. The measurements behind 1024, including why a threshold
    ///     just under the per-keyword cliff is not enough: docs/wiki/03-local-runtime-and-providers.md, "The grammar
    ///     repetition bound".
    /// </remarks>
    internal const int MaxGrammarRepetitionBound = 1024;

    /// <summary>
    ///     Returns <paramref name="schema" /> itself when every bound it carries is already compilable, and otherwise a
    ///     new element with the offending keywords removed.
    /// </summary>
    /// <remarks>
    ///     Returning the input instance keeps the common path — every already-safe tool, every request — free of any
    ///     parse or rewrite allocation.
    /// </remarks>
    internal static JsonElement Sanitize(JsonElement schema)
    {
        if (!RequiresSanitizing(schema))
        {
            return schema;
        }

        var buffer = new ArrayBufferWriter<byte>();

#pragma warning disable MA0045 // Utf8JsonWriter over an in-memory buffer: no I/O to await; synchronous canonical-bytes function.
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteSanitized(schema, writer);
        }
#pragma warning restore MA0045

        using var document = JsonDocument.Parse(buffer.WrittenMemory);

        // Detached clone: the temporary document is disposed on return, so the element must not stay bound to it.
        return document.RootElement.Clone();
    }

    /// <summary>
    ///     True when <paramref name="schema" /> carries at least one bound above
    ///     <see cref="MaxGrammarRepetitionBound" /> anywhere in its tree.
    /// </summary>
    /// <remarks>
    ///     This is the allocation-free pre-check that <see cref="Sanitize" /> and the chat client's per-tool scan both
    ///     run first — hence the manual <c>foreach</c> over the struct enumerators rather than LINQ <c>Any</c>, which
    ///     would box one enumerator per schema node on every tool of every round.
    /// </remarks>
    internal static bool RequiresSanitizing(JsonElement schema)
    {
        switch (schema.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in schema.EnumerateObject())
                {
                    var offending = ExceedsBound(property) || RequiresSanitizing(property.Value);
                    if (offending)
                    {
                        return true;
                    }
                }

                return false;

            case JsonValueKind.Array:
                foreach (var item in schema.EnumerateArray())
                {
                    var offending = RequiresSanitizing(item);
                    if (offending)
                    {
                        return true;
                    }
                }

                return false;

            default:
                return false;
        }
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

    // A `pattern` keyword is dropped WHOLE, having no partial form, and only when one of its quantifiers would unroll past the cap. `{n}` costs n repetitions and
    // `{n,m}` costs m, while `{n,}` costs n plus an unbounded tail llama.cpp emits as a recursive rule rather than by unrolling, so the open form is judged on its lower bound.
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
///     function.
/// </summary>
/// <remarks>
///     It exists solely so the OpenAI adapter serialises a compilable <c>tools</c> array, and is created inside
///     <c>DeferredLlamaServerChatClient</c> on a CLONE of the caller's <see cref="ChatOptions" />, so it is never
///     visible to the layers that resolve and execute tools: the function-invocation middleware, its approval
///     detection and <c>ApprovalRequiredAIFunction</c>'s outermost-type contract all operate on the caller's own
///     untouched tool list. This wrapper is therefore never invoked and never validates an argument.
/// </remarks>
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
