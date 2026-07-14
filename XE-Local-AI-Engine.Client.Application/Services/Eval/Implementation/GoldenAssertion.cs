namespace XE_Local_AI_Engine.Client.Services.Eval.Implementation;

using System.Text.Json;

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
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

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
