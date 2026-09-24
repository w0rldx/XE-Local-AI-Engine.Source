namespace XE_Local_AI_Engine.Client.Services.Eval.Implementation;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
///     The four states a stored assertion string can be in, so the judge treats a corrupt constraint differently from
///     a genuinely absent one.
/// </summary>
/// <remarks>
///     Authoring rejects <see cref="Malformed" /> outright, while the judge, which also runs over legacy and corrupt
///     stored rows, must FAIL such a case rather than silently score it on the rubric.
/// </remarks>
internal enum AssertionParseState
{
    /// <summary>Blank (null/whitespace) string: no deterministic signal was supplied for this case.</summary>
    Absent,

    /// <summary>Parsed cleanly but carries no meaningful (non-blank) phrase — it gates nothing and proves nothing.</summary>
    ValidNoSignal,

    /// <summary>Parsed cleanly and carries at least one meaningful required/forbidden phrase — a usable deterministic gate.</summary>
    ValidWithSignal,

    /// <summary>A non-blank string that failed to parse as assertion JSON — a corrupt/dropped scoring constraint.</summary>
    Malformed
}

/// <summary>
///     Parsed golden assertion, the deterministic phrase-check scoring signal, with null and blank phrases filtered
///     on parse so neither can ever gate a case.
/// </summary>
/// <remarks>
///     An empty required phrase would pass any output, since an empty <c>.All</c> is vacuously true, and an empty
///     forbidden phrase would fail everything. <see cref="HasMeaningfulSignal" /> is <see langword="false" /> when
///     neither array holds a non-blank phrase, because such an assertion proves nothing and must not auto-pass. The
///     judge and <see cref="GoldenConversationService" /> share it, so scoring and validation agree on what is usable.
/// </remarks>
internal sealed class GoldenAssertion
{
    public required IReadOnlyList<string> RequiredPhrases { get; init; }

    public required IReadOnlyList<string> ForbiddenPhrases { get; init; }

    // Web defaults keep camelCase matching, but System.Text.Json otherwise IGNORES members it cannot map, so a typo'd
    // property would parse to an all-empty assertion. Disallowing one, and a duplicate, throws into Malformed instead.
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false
    };

    /// <summary>True when at least one non-blank required or forbidden phrase gates the case.</summary>
    public bool HasMeaningfulSignal => RequiredPhrases.Count > 0 || ForbiddenPhrases.Count > 0;

    /// <summary>
    ///     Parses the stored assertion JSON, filtering out null/blank phrases. Returns <see langword="null" /> on
    ///     malformed JSON (an assertion we cannot read cannot prove the candidate is good).
    /// </summary>
    public static GoldenAssertion? TryParse(string assertionJson)
    {
        RawAssertion? raw;
        try
        {
            raw = JsonSerializer.Deserialize<RawAssertion>(assertionJson, SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (raw is null)
        {
            return null;
        }

        return new GoldenAssertion
        {
            RequiredPhrases = Filter(raw.RequiredPhrases),
            ForbiddenPhrases = Filter(raw.ForbiddenPhrases)
        };
    }

    /// <summary>
    ///     Classifies a stored assertion string into one of the four <see cref="AssertionParseState" /> values.
    /// </summary>
    /// <remarks>
    ///     It distinguishes a blank string from a non-blank one that fails to parse, which <see cref="TryParse" />
    ///     collapses by returning null for both, and populates <paramref name="assertion" /> only when valid.
    /// </remarks>
    public static AssertionParseState Classify(string? assertionJson, out GoldenAssertion? assertion)
    {
        assertion = null;

        if (string.IsNullOrWhiteSpace(assertionJson))
        {
            return AssertionParseState.Absent;
        }

        var parsed = TryParse(assertionJson);
        if (parsed is null)
        {
            return AssertionParseState.Malformed;
        }

        assertion = parsed;
        return parsed.HasMeaningfulSignal ? AssertionParseState.ValidWithSignal : AssertionParseState.ValidNoSignal;
    }

    private static IReadOnlyList<string> Filter(IReadOnlyList<string?>? phrases)
    {
        if (phrases is null)
        {
            return [];
        }

        return [.. phrases.Where(static phrase => !string.IsNullOrWhiteSpace(phrase)).Select(static phrase => phrase!)];
    }

    // Positional record: System.Text.Json binds JSON properties to the constructor parameters by name (Web defaults).
    private sealed record RawAssertion(List<string?>? RequiredPhrases, List<string?>? ForbiddenPhrases);
}
