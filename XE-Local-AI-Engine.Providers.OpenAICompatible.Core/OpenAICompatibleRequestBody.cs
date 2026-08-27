namespace XE_Local_AI_Engine.Providers.OpenAICompatible.Core;

using System.Text;
using Microsoft.Extensions.AI;
// Aliased (not a blanket `using OpenAI.Chat`) so OpenAI.Chat.ChatMessage never collides with the MEAI ChatMessage that
// every IChatClient signature in the consuming projects uses.
using ChatCompletionOptions = OpenAI.Chat.ChatCompletionOptions;

/// <summary>
///     The one supported way to put a body field on an outbound OpenAI-compatible chat request that the typed OpenAI
///     schema does not model — <c>reasoning_budget_tokens</c>, <c>chat_template_kwargs</c>, <c>top_k</c>, … — plus the
///     chaining discipline that keeps two such patches from cancelling each other out.
/// </summary>
/// <remarks>
///     <para>
///         WHY it exists: MEAI's OpenAI adapter DROPS unmapped <see cref="ChatOptions.AdditionalProperties" />, so a
///         non-standard field has to ride the <see cref="ChatCompletionOptions" /> the adapter actually serializes —
///         which is the one <see cref="ChatOptions.RawRepresentationFactory" /> returns. <c>ChatCompletionOptions.Patch</c>
///         (the System.ClientModel JSON patch) is the only seam that writes an arbitrary top-level field onto it.
///     </para>
///     <para>
///         WHY the chaining matters: <see cref="ChatOptions.RawRepresentationFactory" /> is a single slot. A second
///         patch that assigns its own factory without invoking the one already there silently discards the first
///         patch's field — a bug with no compile-time or runtime signal, just a body missing something the caller
///         believes it sent. <see cref="Chain" /> always composes the prior factory, so patches accumulate.
///     </para>
///     <para>
///         WHY it is generic and lives here: the mechanism is pure OpenAI-wire plumbing shared by llama-server and every
///         external OpenAI-compatible endpoint. The runtime-SPECIFIC decisions — which marker key means what, which
///         fields a given server actually reads — deliberately stay in their own provider projects.
///     </para>
/// </remarks>
public static class OpenAICompatibleRequestBody
{
    /// <summary>
    ///     Returns a clone of <paramref name="options" /> whose <see cref="ChatOptions.RawRepresentationFactory" />
    ///     yields a <see cref="ChatCompletionOptions" /> with <paramref name="configure" /> applied, composing (never
    ///     replacing) any factory the options already carried. <paramref name="options" /> itself is never mutated, so
    ///     the caller's instance stays reusable.
    /// </summary>
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

        // SCME0001: ChatCompletionOptions.Patch (System.ClientModel JsonPatch) is [Experimental]. It is the ONLY seam
        // that serializes an arbitrary top-level body field, and MEAI's OpenAI adapter serializes the
        // ChatCompletionOptions this options object becomes, Patch included. Suppression is scoped to the single call.
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
