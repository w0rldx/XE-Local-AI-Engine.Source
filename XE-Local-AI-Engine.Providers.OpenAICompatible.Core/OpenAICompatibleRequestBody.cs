namespace XE_Local_AI_Engine.Providers.OpenAICompatible.Core;

using System.Text;
using Microsoft.Extensions.AI;
// Aliased (not a blanket `using OpenAI.Chat`) so OpenAI.Chat.ChatMessage never collides with the MEAI ChatMessage that
// every IChatClient signature in the consuming projects uses.
using ChatCompletionOptions = OpenAI.Chat.ChatCompletionOptions;

/// <summary>
///     The one supported way to put a body field the typed OpenAI schema does not model on an outbound chat request,
///     plus the chaining discipline that keeps two such patches from cancelling each other out.
/// </summary>
/// <remarks>
///     MEAI's OpenAI adapter DROPS unmapped <see cref="ChatOptions.AdditionalProperties" />, so a non-standard field
///     rides the <see cref="ChatCompletionOptions" /> the adapter serializes, whose <c>Patch</c> is the only seam
///     writing an arbitrary top-level field. Chaining matters because
///     <see cref="ChatOptions.RawRepresentationFactory" /> is a single slot: a second patch assigning its own factory
///     silently discards the first's field, with no signal. Runtime-SPECIFIC decisions stay in their own projects.
/// </remarks>
public static class OpenAICompatibleRequestBody
{
    /// <summary>
    ///     Returns a clone of <paramref name="options" /> whose <see cref="ChatOptions.RawRepresentationFactory" />
    ///     yields a <see cref="ChatCompletionOptions" /> with <paramref name="configure" /> applied.
    /// </summary>
    /// <remarks>
    ///     It COMPOSES, never replaces, any factory the options already carried, and <paramref name="options" /> is
    ///     never mutated, so the caller's instance stays reusable.
    /// </remarks>
    /// <param name="options">The turn's options; must not be <see langword="null" /> (callers short-circuit first).</param>
    /// <param name="configure">Applies the patch to the request body the adapter is about to serialize.</param>
    public static ChatOptions Chain(ChatOptions options, Action<ChatCompletionOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configure);

        var priorFactory = options.RawRepresentationFactory;
        var patched = options.Clone();
        patched.RawRepresentationFactory = client =>
        {
            var baseOptions = priorFactory?.Invoke(client) as ChatCompletionOptions ?? new ChatCompletionOptions();
            configure(baseOptions);
            return baseOptions;
        };
        return patched;
    }

    /// <summary>Writes an integer body field at <paramref name="jsonPath" /> (for example <c>$.top_k</c>).</summary>
    public static void SetField(ChatCompletionOptions body, string jsonPath, int value)
    {
        ArgumentNullException.ThrowIfNull(body);

        // SCME0001: ChatCompletionOptions.Patch is [Experimental] but the ONLY seam serializing an arbitrary top-level
        // body field, and MEAI's adapter serializes this options object Patch included; scoped to the single call.
#pragma warning disable SCME0001
        body.Patch.Set(EncodePath(jsonPath), value);
#pragma warning restore SCME0001
    }

    /// <summary>Writes a single-precision body field at <paramref name="jsonPath" /> (for example <c>$.min_p</c>).</summary>
    public static void SetField(ChatCompletionOptions body, string jsonPath, float value)
    {
        ArgumentNullException.ThrowIfNull(body);

#pragma warning disable SCME0001 // See SetField(ChatCompletionOptions, string, int).
        body.Patch.Set(EncodePath(jsonPath), value);
#pragma warning restore SCME0001
    }

    /// <summary>
    ///     Writes a RAW JSON value at <paramref name="jsonPath" /> — the escape hatch for a field whose value is an
    ///     object or array (for example <c>$.chat_template_kwargs</c> = <c>{"enable_thinking":false}</c>). The caller
    ///     owns the validity of <paramref name="rawJson" />; it is copied onto the body verbatim.
    /// </summary>
    public static void SetRawField(ChatCompletionOptions body, string jsonPath, ReadOnlySpan<byte> rawJson)
    {
        ArgumentNullException.ThrowIfNull(body);

#pragma warning disable SCME0001 // See SetField(ChatCompletionOptions, string, int).
        body.Patch.Set(EncodePath(jsonPath), rawJson);
#pragma warning restore SCME0001
    }

    // The JsonPatch API is span-based; the paths are short ASCII constants and this runs once per request on the cold
    // serialization path, so encoding here keeps every call site readable without a measurable cost.
    private static byte[] EncodePath(string jsonPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jsonPath);
        return Encoding.UTF8.GetBytes(jsonPath);
    }
}
