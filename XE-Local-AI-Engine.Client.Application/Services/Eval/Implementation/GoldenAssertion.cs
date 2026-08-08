namespace XE_Local_AI_Engine.Client.Services.Eval.Implementation;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
///     Distinguishes the four states a stored assertion string can be in, so the judge can treat a corrupt constraint
///     differently from a genuinely-absent one. Authoring rejects <see cref="Malformed" /> outright; the judge, which
///     also runs over legacy/corrupt stored rows, must FAIL such a case rather than silently score it on the rubric.
/// </summary>
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
///     Parsed golden assertion (the deterministic phrase-check scoring signal). Null/blank phrases are filtered on parse
///     so an empty or whitespace phrase can never gate a case — an empty required phrase would "pass" any output
///     (<c>"".Contains("")</c> is true, and an empty <c>.All</c> is vacuously true), an empty forbidden phrase would fail
///     everything. <see cref="HasMeaningfulSignal" /> is <see langword="false" /> when neither array holds a non-blank
///     phrase; such an assertion proves nothing and must not auto-pass. Shared by the judge (deterministic scoring) and
///     <see cref="GoldenConversationService" /> (create/update validation) so both agree on what a usable assertion is.
/// </summary>
internal sealed record GoldenAssertion(IReadOnlyList<string> RequiredPhrases, IReadOnlyList<string> ForbiddenPhrases)
{
    // Web defaults keep camelCase naming + case-insensitive matching so a correctly-spelled wire payload still binds,
    // but System.Text.Json otherwise IGNORES members it cannot map — so {"requiredPhrase":[...]} (a typo / schema drift)
    // would parse into an all-empty assertion, be classified ValidNoSignal, and silently drop the intended deterministic
    // gate to the rubric judge. Disallow makes an unmapped member throw JsonException, which TryParse turns into null →
    // Classify returns Malformed → the judge records an explicit failed case and authoring rejects the input. Likewise a
    // duplicate property is a corrupt payload where the silently-kept last value could differ from the author's intent, so
    // reject it too (.NET 10 JsonException on duplicates).
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

        return new GoldenAssertion(Filter(raw.RequiredPhrases), Filter(raw.ForbiddenPhrases));
    }

    /// <summary>
    ///     Classifies a stored assertion string into one of the four <see cref="AssertionParseState" /> values, so a
    ///     caller can distinguish a blank string (<see cref="AssertionParseState.Absent" />) from a non-blank string that
    ///     fails to parse (<see cref="AssertionParseState.Malformed" />) — a distinction <see cref="TryParse" /> collapses
    ///     (both return <see langword="null" />). <paramref name="assertion" /> is populated only for the two Valid states.
    /// </summary>
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
