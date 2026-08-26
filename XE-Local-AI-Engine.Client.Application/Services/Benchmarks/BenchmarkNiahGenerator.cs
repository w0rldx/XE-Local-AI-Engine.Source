namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Reads a generated case's own parameters back off the item that carries them. The case is self-describing on
///     purpose: the freeze re-checks the probe length against the project window without parsing a haystack back out
///     of a prompt, and the UI has a label without re-deriving one.
/// </summary>
public static class BenchmarkNiahCase
{
    /// <summary>
    ///     The case an item describes, or <see langword="null" /> when the item is not a generated case. Throws when
    ///     it IS one and its parameters cannot be read: a probe nothing can vouch for must not quietly skip the
    ///     length check that exists to stop it measuring the context window instead of the model.
    /// </summary>
    public static BenchmarkNiahCaseV1? TryRead(BenchmarkTaskItemRecord item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!string.Equals(item.Kind, BenchmarkTaskItemKinds.NiahCase, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            return item.GeneratorConfigJson is { IsEmpty: false } json
                ? JsonSerializer.Deserialize<BenchmarkNiahCaseV1>(json.Span, BenchmarkNiahGenerator.SerializerOptions)
                  ?? throw new BenchmarkValidationException("A long-context probe case carries no parameters.")
                : throw new BenchmarkValidationException("A long-context probe case carries no parameters.");
        }
        catch (JsonException exception)
        {
            throw new BenchmarkValidationException($"A long-context probe case carries unreadable parameters: {exception.Message}");
        }
    }
}

/// <summary>
///     A single-needle long-context probe, as an operator configures it. One case is generated per
///     (<see cref="ContextTokens" /> x <see cref="NeedleDepthPercent" />) pair, and every case is an ordinary task
///     item with its own id — so the caps, the staleness hashes and the export all reach it without knowing what NIAH
///     is.
/// </summary>
/// <param name="CriterionId">
///     Which rubric criterion each generated case overrides with its own expected passcode. It must name an
///     <c>exact</c> criterion in the project's judge policy: the case supplies the answer, the policy supplies the
///     kind. Defaults to <see cref="BenchmarkNiahGenerator.DefaultCriterionId" />.
/// </param>
/// <param name="Seed">Mixed into every case's derivation, so two projects can probe the same sizes over different text.</param>
/// <param name="CountsTowardScore">
///     Whether the generated cases enter the project's ranked mean. <see langword="false" /> by default: recall is a
///     capability, not quality, and averaging 0-or-10 recall into a rubric mean says a model that missed the needle
///     wrote a worse answer. The cases are still scored and still reported — on their own axis. The flag lives here
///     rather than on the item draft because the draft's own default is <see langword="true" />, which is right for an
///     authored prompt and wrong for a probe.
/// </param>
public sealed record BenchmarkNiahConfigV1(
    IReadOnlyList<int>? ContextTokens = null,
    IReadOnlyList<int>? NeedleDepthPercent = null,
    string? NeedleTemplate = null,
    string? QuestionTemplate = null,
    string? CriterionId = null,
    int Seed = 0,
    bool CountsTowardScore = false);

/// <summary>
///     What ONE generated case is, kept on the case's own <c>GeneratorConfigJson</c> so the case is self-describing:
///     the freeze re-checks <see cref="ContextTokens" /> against the project window without parsing the haystack back
///     out of the prompt, and the UI has a label without re-deriving one.
/// </summary>
/// <param name="ContextTokens">The REQUESTED probe length. What the refusal compares against the project's window.</param>
/// <param name="ApproximateTokens">
///     What the haystack actually estimates to, by the same approximation the knowledge chunker sizes windows with.
///     It under-counts, which is why the generator targets a fraction of the request rather than the request itself.
/// </param>
/// <param name="Label">
///     The display name, and deliberately hedged (<c>≈32k @ 50%</c>). A probe that silently ran at 26k instead of 32k
///     is worse than one labelled approximate.
/// </param>
public sealed record BenchmarkNiahCaseV1(
    int ContextTokens,
    int DepthPercent,
    int ApproximateTokens,
    int Seed,
    string Label,
    string Subject,
    string Corpus);

/// <summary>
///     Builds the haystacks. Pure and seeded: the same parent id, the same configuration and the same shipped corpus
///     produce the same prompt bytes on every machine, which is what lets a generated case be replayed like any
///     authored one — and what lets its <c>InputHash</c> mean something.
/// </summary>
/// <remarks>
///     Deliberately expanded at item-WRITE time rather than at freeze. A case generated during a freeze would have no
///     durable identity: nothing to stamp on the run, nothing for the caps to count, and no way for the ranking read
///     to know how many probes a cell owed.
/// </remarks>
public static class BenchmarkNiahGenerator
{
    /// <summary>The rubric criterion a case overrides when the configuration names none.</summary>
    public const string DefaultCriterionId = "recall";

    public const string DefaultNeedleTemplate = "The secret passcode for {city} is {code}.";
    public const string DefaultQuestionTemplate = "What is the secret passcode for {city}?";

    /// <summary>
    ///     The fraction of the requested length the haystack is actually built to.
    ///     <para>
    ///         The token count is an approximation — weighted characters over four — and it under-counts English
    ///         prose, so building to the full request would overshoot the real tokenization and truncate the tail of
    ///         the haystack inside the model's window. Truncation is the one failure a recall probe must not have:
    ///         a needle that fell off the end measures the window, not the model. So the generator aims low, and the
    ///         label says it aims low.
    ///     </para>
    /// </summary>
    public const double TargetFraction = 0.90;

    /// <summary>Below this a haystack is too short to hide anything and the depths stop being distinguishable.</summary>
    public const int MinimumContextTokens = 512;

    /// <summary>How many (size x depth) pairs one generator may expand into, before the item cap even applies.</summary>
    public const int MaximumCases = 20;

    private const string PasscodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const int PasscodeLength = 6;

    /// <summary>
    ///     The subjects a needle is written about. A fixed list rather than a corpus-derived one: the question has to
    ///     name the subject unambiguously, and a name lifted out of wikitext may occur a hundred more times in the
    ///     haystack — which turns a recall probe into a disambiguation one.
    /// </summary>
    private static readonly string[] Subjects =
    [
        "Lisbon", "Reykjavik", "Montevideo", "Ulaanbaatar", "Ljubljana", "Wellington",
        "Gaborone", "Tbilisi", "Kingston", "Helsinki", "Windhoek", "Chisinau"
    ];

    /// <summary>
    ///     The shipped wikitext-2-raw test split, cut into sentences once per process. The file is 1.3 MB and the cut
    ///     is deterministic, so every case in every project draws from the same array.
    /// </summary>
    private static readonly Lazy<HaystackCorpus> Corpus = new(LoadCorpus, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>One generated case: the item to write, and the answer only the verifier is told.</summary>
    /// <param name="ExpectedAnswer">The passcode. It reaches the case's encrypted verifier override and nothing else.</param>
    public sealed record GeneratedCase(BenchmarkNiahCaseV1 Case, string Prompt, string ExpectedAnswer);

    /// <summary>
    ///     Every case a generator expands into, in (size, depth) order. Pure: no clock, no store, no randomness that
    ///     is not derived from <paramref name="parentItemId" /> and the configuration.
    /// </summary>
    /// <param name="projectContextTokens">
    ///     The project's frozen window. A case longer than it is refused HERE, while the operator is still looking at
    ///     the form, rather than an hour into a batch — and the freeze re-checks it anyway.
    /// </param>
    public static IReadOnlyList<GeneratedCase> Expand(Guid parentItemId, BenchmarkNiahConfigV1 config, int projectContextTokens)
    {
        ArgumentNullException.ThrowIfNull(config);
        var sizes = Ordered(config.ContextTokens, "contextTokens");
        var depths = Ordered(config.NeedleDepthPercent, "needleDepthPercent");
        var needleTemplate = Template(config.NeedleTemplate, DefaultNeedleTemplate, "needleTemplate", requiresCode: true);
        var questionTemplate = Template(config.QuestionTemplate, DefaultQuestionTemplate, "questionTemplate", requiresCode: false);

        if (sizes.Count * depths.Count > MaximumCases)
        {
            throw new BenchmarkValidationException(
                $"A long-context probe expands into {sizes.Count * depths.Count} cases ({sizes.Count} lengths x {depths.Count} depths). "
                + $"The maximum is {MaximumCases}.");
        }

        if (sizes.FirstOrDefault(static size => size < MinimumContextTokens) is var tooShort and > 0)
        {
            throw new BenchmarkValidationException($"A long-context probe of {tooShort} tokens is shorter than the {MinimumContextTokens}-token floor.");
        }

        // Refused at EXPANSION as well as at freeze. A probe silently truncated to the project window measures the
        // window; naming both numbers is the difference between a fixable form error and a wasted batch.
        if (sizes.FirstOrDefault(size => size > projectContextTokens) is var tooLong and > 0)
        {
            throw new BenchmarkValidationException(
                $"A long-context probe of {tooLong} tokens does not fit the project's {projectContextTokens}-token context window.");
        }

        if (depths.Where(static depth => depth is < 0 or > 100).Select(static depth => (int?)depth).FirstOrDefault() is { } outside)
        {
            throw new BenchmarkValidationException($"A needle depth of {outside}% is outside 0..100.");
        }

        var corpus = Corpus.Value;
        var cases = new List<GeneratedCase>(sizes.Count * depths.Count);
        foreach (var size in sizes)
        {
            foreach (var depth in depths)
            {
                cases.Add(Build(parentItemId, config.Seed, size, depth, needleTemplate, questionTemplate, corpus));
            }
        }

        return cases;
    }

    /// <summary>
    ///     The <c>exact</c> criterion override one case carries, as the item's verifier config: <c>{criterionId:
    ///     {expected, normalize}}</c>. Case-insensitive and whitespace-collapsing, because a recall probe is asking
    ///     whether the model FOUND the passcode, not whether it echoed the shift key.
    /// </summary>
    public static string VerifierConfigJson(string criterionId, string expectedAnswer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(criterionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedAnswer);
        return JsonSerializer.Serialize(new Dictionary<string, ExactOverride>(StringComparer.Ordinal)
        {
            [criterionId] = new(expectedAnswer, new BenchmarkVerifierNormalizeV1(Trim: true, CollapseWhitespace: true, CaseInsensitive: true, StripMarkdown: true))
        }, SerializerOptions);
    }

    /// <summary>
    ///     Web naming, matching what <see cref="BenchmarkJudgeVerifierConfig" /> parses a criterion config back with.
    ///     The two have to agree: this writes the blob that one reads.
    /// </summary>
    internal static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false
    };

    /// <summary>The criterion id a configuration names, or the default.</summary>
    public static string CriterionIdOf(BenchmarkNiahConfigV1 config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return string.IsNullOrWhiteSpace(config.CriterionId) ? DefaultCriterionId : config.CriterionId.Trim();
    }

    private static GeneratedCase Build(Guid parentItemId,
        int seed,
        int contextTokens,
        int depthPercent,
        string needleTemplate,
        string questionTemplate,
        HaystackCorpus corpus)
    {
        // Every case derives its own stream from the parent, the size, the depth and the operator's seed, so two
        // cases of one generator draw different text and the same case redraws the same text forever.
        var random = SplitMix64.Seeded(parentItemId, contextTokens, depthPercent, seed);
        var subject = Subjects[(int)(random.Next() % (ulong)Subjects.Length)];
        var passcode = Passcode(random);
        var needle = needleTemplate.Replace("{city}", subject, StringComparison.Ordinal).Replace("{code}", passcode, StringComparison.Ordinal);
        var question = questionTemplate.Replace("{city}", subject, StringComparison.Ordinal).Replace("{code}", passcode, StringComparison.Ordinal);

        // Overshoot then trim: sentences are taken until the estimate passes the target and the last one is dropped,
        // so the haystack ends on a sentence boundary at or below target rather than mid-clause above it.
        var targetTokens = (int)(contextTokens * TargetFraction);
        var start = (int)(random.Next() % (ulong)corpus.Sentences.Length);
        var selected = new List<string>();
        var weighted = 0L;
        var budget = (long)targetTokens * ChunkTokenApproximation.CharsPerToken;
        for (var taken = 0; taken < corpus.Sentences.Length; taken++)
        {
            var sentence = corpus.Sentences[(start + taken) % corpus.Sentences.Length];
            var cost = ChunkTokenApproximation.WeightedLength(sentence) + 1;
            if (weighted + cost > budget && selected.Count > 0)
            {
                break;
            }

            selected.Add(sentence);
            weighted += cost;
        }

        // Placed by WEIGHT, not by sentence index: the sentences are of wildly different lengths, so "the 50th of 100
        // sentences" and "halfway through the text" are not the same position, and the depth axis is about the second.
        var threshold = weighted * depthPercent / 100;
        var running = 0L;
        var insertAt = selected.Count;
        for (var index = 0; index < selected.Count; index++)
        {
            if (running >= threshold)
            {
                insertAt = index;
                break;
            }

            running += ChunkTokenApproximation.WeightedLength(selected[index]) + 1;
        }

        selected.Insert(insertAt, needle);
        var haystack = string.Join(' ', selected);
        var prompt = BuildPrompt(haystack, question, corpus.Attribution);
        var approximateTokens = ChunkTokenApproximation.EstimateTokens(prompt);
        var label = string.Create(CultureInfo.InvariantCulture, $"NIAH ≈{Kilo(contextTokens)} @ {depthPercent}%");
        return new GeneratedCase(
            new BenchmarkNiahCaseV1(contextTokens, depthPercent, approximateTokens, seed, label, subject, corpus.CorpusId),
            prompt,
            passcode);
    }

    /// <summary>
    ///     The probe as the model sees it. The answer instruction is emphatic because the case is graded by exact
    ///     match with no judge model behind it: a correct passcode wrapped in a sentence scores the same as a wrong
    ///     one, and the only defence against measuring formatting instead of recall is asking plainly.
    /// </summary>
    private static string BuildPrompt(string haystack, string question, string attribution)
    {
        var builder = new StringBuilder(haystack.Length + 512);
        _ = builder.AppendLine("Read the following document carefully. One sentence in it states a secret passcode.")
                   .AppendLine()
                   .AppendLine("<document>")
                   .AppendLine(haystack)
                   .AppendLine("</document>")
                   .AppendLine()
                   .Append(question)
                   .AppendLine()
                   .AppendLine("Answer with the passcode only — no explanation, no punctuation, no other words.")
                   .AppendLine()
                   .Append("(Document text: ").Append(attribution).Append(')');
        return builder.ToString();
    }

    private static string Passcode(SplitMix64 random)
    {
        return string.Create(PasscodeLength, random, static (span, generator) =>
        {
            for (var index = 0; index < span.Length; index++)
            {
                span[index] = PasscodeAlphabet[(int)(generator.Next() % (ulong)PasscodeAlphabet.Length)];
            }
        });
    }

    private static string Kilo(int tokens) =>
        tokens >= 1024 && tokens % 1024 == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{tokens / 1024}k")
            : tokens.ToString(CultureInfo.InvariantCulture);

    private static IReadOnlyList<int> Ordered(IReadOnlyList<int>? values, string name)
    {
        if (values is null || values.Count == 0)
        {
            throw new BenchmarkValidationException($"A long-context probe needs at least one '{name}' value.");
        }

        // Distinct and ordered, so two operators who typed the same set in different orders get the same cases in the
        // same indices — the generator config is inside the item's input hash.
        return [.. values.Distinct().Order()];
    }

    private static string Template(string? value, string fallback, string name, bool requiresCode)
    {
        var template = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        if (!template.Contains("{city}", StringComparison.Ordinal))
        {
            throw new BenchmarkValidationException($"The '{name}' must contain the {{city}} placeholder.");
        }

        if (requiresCode && !template.Contains("{code}", StringComparison.Ordinal))
        {
            throw new BenchmarkValidationException($"The '{name}' must contain the {{code}} placeholder.");
        }

        return template;
    }

    private static HaystackCorpus LoadCorpus()
    {
        var file = BenchmarkFidelityCorpus.Require();
        var text = File.ReadAllText(file.Path);
        var sentences = new List<string>(1 << 15);
        foreach (var line in text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            // Headings are ' = Title = ' lines: dropped, because a haystack of section titles reads as a table of
            // contents rather than as prose, and the needle would stand out by being the only sentence in it.
            if (line.StartsWith('=') || line.Length < 40)
            {
                continue;
            }

            // wikitext-2-raw is space-tokenized, so a sentence ends at ' . '. Split on it and put the period back.
            foreach (var part in line.Split(" . ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (part.Length >= 40)
                {
                    sentences.Add(part.TrimEnd('.', ' ') + ".");
                }
            }
        }

        if (sentences.Count == 0)
        {
            throw new InvalidOperationException("The benchmark corpus yielded no usable haystack sentences.");
        }

        return new HaystackCorpus([.. sentences],
            file.CorpusId,
            $"WikiText-2 ({file.CorpusId}), Salesforce Research, CC BY-SA 3.0");
    }

    private sealed record HaystackCorpus(string[] Sentences, string CorpusId, string Attribution);

    private sealed record ExactOverride(string Expected, BenchmarkVerifierNormalizeV1 Normalize);

    /// <summary>
    ///     A fixed 64-bit generator rather than <see cref="Random" />. <c>Random</c>'s sequence is an implementation
    ///     detail the runtime has changed before, and a case whose text changes with a .NET upgrade would move its own
    ///     input hash and unrank every answer ever given to it.
    /// </summary>
    private sealed class SplitMix64(ulong state)
    {
        private ulong _state = state;

        public static SplitMix64 Seeded(Guid parentItemId, int contextTokens, int depthPercent, int seed)
        {
            Span<byte> material = stackalloc byte[16 + (3 * sizeof(int))];
            _ = parentItemId.TryWriteBytes(material);
            BitConverter.TryWriteBytes(material[16..], contextTokens);
            BitConverter.TryWriteBytes(material[20..], depthPercent);
            BitConverter.TryWriteBytes(material[24..], seed);
            Span<byte> digest = stackalloc byte[32];
            _ = SHA256.HashData(material, digest);
            return new SplitMix64(BitConverter.ToUInt64(digest));
        }

        public ulong Next()
        {
            _state += 0x9E3779B97F4A7C15UL;
            var value = _state;
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            return value ^ (value >> 31);
        }
    }
}
