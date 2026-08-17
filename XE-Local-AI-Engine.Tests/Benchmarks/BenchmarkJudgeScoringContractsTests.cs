namespace XE_Local_AI_Engine.Tests.Benchmarks;

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class BenchmarkJudgeScoringContractsTests
{
    private const string HappyPath =
        """{"schemaVersion":2,"criteria":[{"id":"alpha","score":8,"rationale":"solid"},{"id":"beta","score":4,"rationale":"partial"}],"summary":"Mostly right."}""";

    [Test]
    public void Parser_ReadsCriteriaAndComputesWeightedScore()
    {
        var result = BenchmarkJudgeResultParser.Parse(HappyPath, Rubric(("alpha", 1), ("beta", 1)), "v1:fingerprint");

        AssertEx.Equal(2, result.SchemaVersion);
        AssertEx.Equal(60, result.Score);
        AssertEx.Equal("Mostly right.", result.Summary);
        AssertEx.Equal("v1:fingerprint", result.JudgeModelContentFingerprint);
        AssertEx.Equal(2, result.Criteria.Count);
        AssertEx.ContainsSingle(result.Criteria, criterion => criterion.Id == "alpha" && criterion.Score == 8 && criterion.Rationale == "solid");
        AssertEx.ContainsSingle(result.Criteria, criterion => criterion.Id == "beta" && criterion.Score == 4 && criterion.Rationale == "partial");
    }

    [Test]
    public void Parser_IsInsensitiveToCriterionOrder()
    {
        const string reversed =
            """{"schemaVersion":2,"criteria":[{"id":"beta","score":4,"rationale":"partial"},{"id":"alpha","score":8,"rationale":"solid"}],"summary":"Mostly right."}""";

        var forwardScore = BenchmarkJudgeResultParser.Parse(HappyPath, Rubric(("alpha", 1), ("beta", 1)), "v1:fingerprint").Score;
        var reversedScore = BenchmarkJudgeResultParser.Parse(reversed, Rubric(("alpha", 1), ("beta", 1)), "v1:fingerprint").Score;

        AssertEx.Equal(forwardScore, reversedScore);
    }

    [Test]
    public void Parser_AcceptsBoundaryRationaleAndSummaryLengths()
    {
        var rationale = new string('r', 2048);
        var summary = new string('s', 4096);
        var payload = $$"""{"schemaVersion":2,"criteria":[{"id":"alpha","score":0,"rationale":"{{rationale}}"}],"summary":"{{summary}}"}""";

        var result = BenchmarkJudgeResultParser.Parse(payload, Rubric(("alpha", 1)), "v1:fingerprint");

        AssertEx.Equal(2048, result.Criteria[0].Rationale.Length);
        AssertEx.Equal(4096, result.Summary.Length);
        AssertEx.Equal(0, result.Score);
    }

    [Test]
    public void Parser_RejectsEveryMalformedShape()
    {
        var rubric = Rubric(("alpha", 1), ("beta", 1));
        string[] malformed =
        [
            """{"schemaVersion":2,"criteria":[{"id":"alpha","score":8,"rationale":"solid"},{"id":"beta","score":4,"rationale":"partial"}],"summary":"ok","extra":1}""",
            """{"schemaVersion":2,"criteria":[{"id":"alpha","score":8,"rationale":"solid"},{"id":"beta","score":4,"rationale":"partial"}]}""",
            """{"schemaVersion":1,"criteria":[{"id":"alpha","score":8,"rationale":"solid"},{"id":"beta","score":4,"rationale":"partial"}],"summary":"ok"}""",
            """{"schemaVersion":"2","criteria":[{"id":"alpha","score":8,"rationale":"solid"},{"id":"beta","score":4,"rationale":"partial"}],"summary":"ok"}""",
            """{"schemaVersion":2,"criteria":[{"id":"alpha","score":8,"rationale":"a"},{"id":"beta","score":4,"rationale":"b"},{"id":"gamma","score":4,"rationale":"c"}],"summary":"ok"}""",
            """{"schemaVersion":2,"criteria":[{"id":"alpha","score":8,"rationale":"a"}],"summary":"ok"}""",
            """{"schemaVersion":2,"criteria":[{"id":"alpha","score":8,"rationale":"a"},{"id":"alpha","score":4,"rationale":"b"}],"summary":"ok"}""",
            """{"schemaVersion":2,"criteria":[{"id":"alpha","score":11,"rationale":"a"},{"id":"beta","score":4,"rationale":"b"}],"summary":"ok"}""",
            """{"schemaVersion":2,"criteria":[{"id":"alpha","score":-1,"rationale":"a"},{"id":"beta","score":4,"rationale":"b"}],"summary":"ok"}""",
            """{"schemaVersion":2,"criteria":[{"id":"alpha","score":2,"rationale":"a"},{"id":"beta","score":4,"rationale":"b"}],"summary":"ok","criteria2":[]}""",
            """{"schemaVersion":2,"criteria":[{"id":"alpha","score":2,"rationale":"   "},{"id":"beta","score":4,"rationale":"b"}],"summary":"ok"}""",
            """{"schemaVersion":2,"criteria":[{"id":"alpha","score":2,"rationale":"a"},{"id":"beta","score":4,"rationale":"b"}],"summary":""}""",
            """{"schemaVersion":2,"criteria":[{"id":"alpha","score":2,"rationale":"a","extra":1},{"id":"beta","score":4,"rationale":"b"}],"summary":"ok"}""",
            """{"schemaVersion":2,"criteria":[{"id":"alpha","score":2},{"id":"beta","score":4,"rationale":"b"}],"summary":"ok"}""",
            """{"schemaVersion":2,"criteria":{"alpha":2},"summary":"ok"}""",
            """```json {"schemaVersion":2,"criteria":[{"id":"alpha","score":2,"rationale":"a"},{"id":"beta","score":4,"rationale":"b"}],"summary":"ok"} ```""",
            """{"schemaVersion":2,"criteria":[{"id":"alpha","score":2,"rationale":"a"},{"id":"beta","score":4,"rationale":"b"}],"summary":"ok"} trailing""",
            "[]",
            "not json"
        ];

        foreach (var payload in malformed)
        {
            _ = AssertEx.Throws<BenchmarkExecutionException>(() => BenchmarkJudgeResultParser.Parse(payload, rubric, "v1:fingerprint"), payload);
        }
    }

    [Test]
    public void Parser_RejectsOverlongRationaleAndSummary()
    {
        var rationale = new string('r', 2049);
        var summary = new string('s', 4097);

        _ = AssertEx.Throws<BenchmarkExecutionException>(() => BenchmarkJudgeResultParser.Parse(
            $$"""{"schemaVersion":2,"criteria":[{"id":"alpha","score":1,"rationale":"{{rationale}}"}],"summary":"ok"}""",
            Rubric(("alpha", 1)),
            "v1:fingerprint"));
        _ = AssertEx.Throws<BenchmarkExecutionException>(() => BenchmarkJudgeResultParser.Parse(
            $$"""{"schemaVersion":2,"criteria":[{"id":"alpha","score":1,"rationale":"a"}],"summary":"{{summary}}"}""",
            Rubric(("alpha", 1)),
            "v1:fingerprint"));
    }

    [Test]
    public void Calculator_MatchesTheBoundaryTable()
    {
        AssertEx.Equal(100, Compute([("a", 100, 10), ("b", 100, 10), ("c", 100, 10)]));
        AssertEx.Equal(0, Compute([("a", 100, 0), ("b", 100, 0), ("c", 100, 0)]));
        AssertEx.Equal(50, Compute([("a", 1, 5)]));
        AssertEx.Equal(5, Compute([("a", 1, 0), ("b", 1, 1)]));
        AssertEx.Equal(3, Compute([("a", 1, 0), ("b", 1, 0), ("c", 1, 1)]));
        AssertEx.Equal(3, Compute([("a", 1, 1), ("b", 2, 0)]));
        AssertEx.Equal(8, Compute([("a", 3, 1), ("b", 1, 0)]));
    }

    [Test]
    public void Calculator_HandlesTheExtremeRubricWithoutOverflow()
    {
        var criteria = Enumerable.Range(0, 8).Select(index => ($"c{index}", 100, 10)).ToArray();

        AssertEx.Equal(100, Compute(criteria));
        AssertEx.Equal(0, Compute([.. criteria.Select(static criterion => (criterion.Item1, criterion.Item2, 0))]));
    }

    [Test]
    public void Calculator_RejectsMissingAndDuplicateCriteria()
    {
        var rubric = Rubric(("alpha", 1), ("beta", 1));

        _ = AssertEx.Throws<BenchmarkExecutionException>(() => BenchmarkJudgeScoreCalculator.Compute(rubric, [Score("alpha", 1)]));
        _ = AssertEx.Throws<BenchmarkExecutionException>(() => BenchmarkJudgeScoreCalculator.Compute(rubric, [Score("alpha", 1), Score("alpha", 1)]));
        _ = AssertEx.Throws<BenchmarkExecutionException>(() => BenchmarkJudgeScoreCalculator.Compute(rubric, [Score("alpha", 1), Score("gamma", 1)]));
        _ = AssertEx.Throws<BenchmarkExecutionException>(() => BenchmarkJudgeScoreCalculator.Compute(rubric, [Score("alpha", 11), Score("beta", 1)]));
    }

    [Test]
    public void OutputSchema_IsWellFormedAndDescribesTheV2Shape()
    {
        using var document = JsonDocument.Parse(BenchmarkJudgeOutputSchemaV2.Json);
        var properties = document.RootElement.GetProperty("properties");
        var items = properties.GetProperty("criteria").GetProperty("items");

        AssertEx.False(document.RootElement.GetProperty("additionalProperties").GetBoolean());
        AssertEx.Equal(2, properties.GetProperty("schemaVersion").GetProperty("const").GetInt32());
        AssertEx.Equal(1, properties.GetProperty("criteria").GetProperty("minItems").GetInt32());
        AssertEx.Equal(8, properties.GetProperty("criteria").GetProperty("maxItems").GetInt32());
        AssertEx.False(items.GetProperty("additionalProperties").GetBoolean());
        AssertEx.Equal(0, items.GetProperty("properties").GetProperty("score").GetProperty("minimum").GetInt32());
        AssertEx.Equal(10, items.GetProperty("properties").GetProperty("score").GetProperty("maximum").GetInt32());
        AssertEx.Equal(2048, items.GetProperty("properties").GetProperty("rationale").GetProperty("maxLength").GetInt32());
        AssertEx.Equal(4096, properties.GetProperty("summary").GetProperty("maxLength").GetInt32());
    }

    // The builder frames, it never re-serializes: it embeds the GRADED parts JSON (coalesced, reasoning-free — see
    // BenchmarkOutputParts.ForJudge) exactly as the caller hands it over, under the pinned property order.
    [Test]
    public void Prompt_OmitsReferenceAnswerWhenNullAndEmbedsRawJson()
    {
        var rubric = Rubric(("alpha", 1));
        const string taskJson = "\"Write a haiku.\"";
        var outputParts = Encoding.UTF8.GetString(
            BenchmarkExecutionSerialization.SerializeParts(BenchmarkOutputParts.ForJudge(
                [
                    new BenchmarkOutputPart("reasoning", Content: "hidden"),
                    new BenchmarkOutputPart("output", Content: "hello")
                ],
                judgeContextTokens: 4096)));

        var withoutReference = BenchmarkJudgePromptV2.BuildUserPayloadJson(taskJson, null, rubric, outputParts, BenchmarkJudgeOutputSchemaV2.Json);
        var withReference = BenchmarkJudgePromptV2.BuildUserPayloadJson(taskJson, "the reference", rubric, outputParts, BenchmarkJudgeOutputSchemaV2.Json);

        using var bare = JsonDocument.Parse(withoutReference);
        using var referenced = JsonDocument.Parse(withReference);
        AssertEx.False(bare.RootElement.TryGetProperty("referenceAnswer", out _));
        AssertEx.Equal("the reference", referenced.RootElement.GetProperty("referenceAnswer").GetString());
        AssertEx.True(referenced.RootElement.EnumerateObject().Select(static property => property.Name)
                                .SequenceEqual(["task", "referenceAnswer", "rubric", "primaryOutputParts", "outputSchema"], StringComparer.Ordinal));
        AssertEx.Equal("Write a haiku.", bare.RootElement.GetProperty("task").GetString());
        AssertEx.Equal(JsonValueKind.Array, bare.RootElement.GetProperty("primaryOutputParts").ValueKind);
        AssertEx.Equal(expected: 1, bare.RootElement.GetProperty("primaryOutputParts").GetArrayLength());
        AssertEx.Equal("output", bare.RootElement.GetProperty("primaryOutputParts")[0].GetProperty("kind").GetString());
        AssertEx.Equal("hello", bare.RootElement.GetProperty("primaryOutputParts")[0].GetProperty("content").GetString());
        AssertEx.Equal(JsonValueKind.Object, bare.RootElement.GetProperty("outputSchema").ValueKind);
        AssertEx.Equal("object", bare.RootElement.GetProperty("outputSchema").GetProperty("type").GetString());
        AssertEx.Equal(JsonValueKind.Array, bare.RootElement.GetProperty("rubric").GetProperty("criteria").ValueKind);
        AssertEx.Equal("alpha", bare.RootElement.GetProperty("rubric").GetProperty("criteria")[0].GetProperty("id").GetString());
    }

    [Test]
    public void Prompt_SystemPromptNamesTheRubricAndForbidsMarkdown()
    {
        AssertEx.Contains(BenchmarkJudgePromptV2.SystemPrompt, "rubric");
        AssertEx.Contains(BenchmarkJudgePromptV2.SystemPrompt, "exactly one JSON object");
        AssertEx.Contains(BenchmarkJudgePromptV2.SystemPrompt, "no markdown");

        // Length neutrality is unconditional: an LLM judge rewarding a longer answer for being longer is the bias this
        // sentence exists to counter, and it must reach a judging of a COMPLETE answer too, not only a truncated one.
        AssertEx.Contains(BenchmarkJudgePromptV2.SystemPrompt, "Do not reward length or verbosity");
        AssertEx.Contains(BenchmarkJudgePromptV2.SystemPromptFor(primaryOutputTruncated: false), "Do not reward length or verbosity");
    }

    // Neither the payload nor the prompt text is hashed into the policy or the JudgeExecutionKey, so an unconditional
    // sentence would silently change what every judging was asked without any version moving. Both extras are therefore
    // conditional, and a complete run's request must stay byte-identical to the one this has always produced.
    [Test]
    public void Prompt_MarksTheTruncatedPrimaryOutputAndOtherwiseStaysByteIdentical()
    {
        var rubric = Rubric(("alpha", 1));
        const string taskJson = "\"Write a haiku.\"";
        var outputParts = Encoding.UTF8.GetString(
            BenchmarkExecutionSerialization.SerializeParts([new BenchmarkOutputPart("output", Content: "hello")]));

        var complete = BenchmarkJudgePromptV2.BuildUserPayloadJson(taskJson, null, rubric, outputParts, BenchmarkJudgeOutputSchemaV2.Json);
        var defaulted = BenchmarkJudgePromptV2.BuildUserPayloadJson(taskJson, null, rubric, outputParts, BenchmarkJudgeOutputSchemaV2.Json,
            primaryOutputTruncated: false);
        var truncated = BenchmarkJudgePromptV2.BuildUserPayloadJson(taskJson, null, rubric, outputParts, BenchmarkJudgeOutputSchemaV2.Json,
            primaryOutputTruncated: true);

        AssertEx.Equal(complete, defaulted, "The default must reproduce the payload the builder produced before the flag existed.");
        using var completeDocument = JsonDocument.Parse(complete);
        using var truncatedDocument = JsonDocument.Parse(truncated);
        AssertEx.False(completeDocument.RootElement.TryGetProperty("primaryOutputTruncated", out _),
            "A complete run's payload must not carry the flag at all.");
        AssertEx.True(truncatedDocument.RootElement.GetProperty("primaryOutputTruncated").GetBoolean());
        AssertEx.True(truncatedDocument.RootElement.EnumerateObject().Select(static property => property.Name)
                                       .SequenceEqual(["task", "rubric", "primaryOutputParts", "primaryOutputTruncated", "outputSchema"],
                                           StringComparer.Ordinal));

        AssertEx.Equal(BenchmarkJudgePromptV2.SystemPrompt, BenchmarkJudgePromptV2.SystemPromptFor(primaryOutputTruncated: false));
        AssertEx.Contains(BenchmarkJudgePromptV2.SystemPromptFor(primaryOutputTruncated: true), "cut off by the token budget");
        AssertEx.Contains(BenchmarkJudgePromptV2.SystemPromptFor(primaryOutputTruncated: true), "incomplete");
    }

    // llama.cpp compiles the response-format schema into a GBNF grammar, and a repetition bound in it breaks the
    // sampler's initialization outright. The constrained-decoding copy therefore carries NO bounds — and must otherwise
    // be the SAME schema as the one the prompt documents, or the decode and the parser would disagree by construction.
    [Test]
    public void ResponseFormatSchema_IsTheDocumentedSchemaWithEveryRepetitionBoundRemoved()
    {
        using var responseFormat = JsonDocument.Parse(BenchmarkJudgeOutputSchemaV2.ResponseFormatJson);
        using var documented = JsonDocument.Parse(BenchmarkJudgeOutputSchemaV2.Json);

        AssertEx.Empty(BoundKeys(responseFormat.RootElement), "The constrained-decoding schema must carry no repetition bounds.");
        AssertEx.False(BoundKeys(documented.RootElement).Count == 0, "The documented schema is the bounded one.");
        AssertEx.Equal(JsonSerializer.Serialize(StripBounds(documented.RootElement)),
            JsonSerializer.Serialize(StripBounds(responseFormat.RootElement)),
            "The two schemas must differ ONLY by the repetition bounds.");

        // The bounds that are NOT repetition bounds stay: they are what stops a model handing itself an 11.
        var score = responseFormat.RootElement.GetProperty("properties").GetProperty("criteria")
                                  .GetProperty("items").GetProperty("properties").GetProperty("score");
        AssertEx.Equal("integer", score.GetProperty("type").GetString());
        AssertEx.Equal(BenchmarkJudgeOutputSchemaV2.MinimumCriterionScore, score.GetProperty("minimum").GetInt32());
        AssertEx.Equal(BenchmarkJudgeOutputSchemaV2.MaximumCriterionScore, score.GetProperty("maximum").GetInt32());
        AssertEx.Equal(BenchmarkJudgePolicyVersions.OutputSchemaVersion,
            responseFormat.RootElement.GetProperty("properties").GetProperty("schemaVersion").GetProperty("const").GetInt32());
        AssertEx.False(responseFormat.RootElement.GetProperty("additionalProperties").GetBoolean());
    }

    private static readonly string[] RepetitionBoundKeys = ["minLength", "maxLength", "minItems", "maxItems"];

    private static List<string> BoundKeys(JsonElement element)
    {
        var found = new List<string>();
        Walk(element, found);
        return found;

        static void Walk(JsonElement current, List<string> found)
        {
            switch (current.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in current.EnumerateObject())
                    {
                        if (RepetitionBoundKeys.Contains(property.Name, StringComparer.Ordinal))
                        {
                            found.Add(property.Name);
                        }

                        Walk(property.Value, found);
                    }

                    break;
                case JsonValueKind.Array:
                    foreach (var item in current.EnumerateArray())
                    {
                        Walk(item, found);
                    }

                    break;
                default:
                    break;
            }
        }
    }

    /// <summary>The element with every repetition-bound property removed, so the two schemas can be compared directly.</summary>
    private static JsonNode? StripBounds(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var mapped = new JsonObject();
                foreach (var property in element.EnumerateObject().Where(static property => !RepetitionBoundKeys.Contains(property.Name, StringComparer.Ordinal)))
                {
                    mapped[property.Name] = StripBounds(property.Value);
                }

                return mapped;
            case JsonValueKind.Array:
                var items = new JsonArray();
                foreach (var item in element.EnumerateArray())
                {
                    items.Add(StripBounds(item));
                }

                return items;
            default:
                return JsonNode.Parse(element.GetRawText());
        }
    }

    [Test]
    public void Serialization_RoundTripsTheVerdictThroughTheWritersOwnOptions_AndDefaultOptionsWouldZeroIt()
    {
        // The live bug this pins: the writer uses JsonSerializerDefaults.Web (camelCase) and a reader that re-derives
        // DEFAULT (PascalCase) options binds every property to its default, so the API answered a zeroed verdict with a
        // null summary — a shape the frontend's schema rejects, taking the whole run detail down with it.
        var written = BenchmarkJudgeSerialization.SerializeResult(new BenchmarkJudgeResultV2(BenchmarkJudgePolicyVersions.OutputSchemaVersion,
            [new BenchmarkJudgeCriterionScoreV2("alpha", 8, "solid")],
            "Mostly right.",
            80,
            $"v1:{new string('b', 64)}"));

        AssertEx.Contains(Encoding.UTF8.GetString(written), "\"schemaVersion\":2");

        var roundTripped = AssertEx.NotNull(BenchmarkJudgeSerialization.DeserializeResult(written));
        AssertEx.Equal(BenchmarkJudgePolicyVersions.OutputSchemaVersion, roundTripped.SchemaVersion);
        AssertEx.Equal("Mostly right.", roundTripped.Summary);
        AssertEx.Equal(80, roundTripped.Score);
        AssertEx.Equal(1, roundTripped.Criteria.Count);
        AssertEx.Equal("alpha", roundTripped.Criteria[0].Id);
        AssertEx.Equal(8, roundTripped.Criteria[0].Score);

        var withDefaultOptions = AssertEx.NotNull(JsonSerializer.Deserialize<BenchmarkJudgeResultV2>(written));
        AssertEx.Equal(0, withDefaultOptions.Score,
            "If this ever binds, camelCase stopped being the stored shape — revisit the pin rather than deleting it.");
    }

    [Test]
    [Arguments("")]
    [Arguments("not json")]
    public void Serialization_ForAnAbsentOrUnreadableVerdict_IsNullRatherThanAThrow(string payload)
    {
        AssertEx.Null(BenchmarkJudgeSerialization.DeserializeResult(Encoding.UTF8.GetBytes(payload)));
        AssertEx.Null(BenchmarkJudgeSerialization.DeserializeResult(null));
    }

    private static int Compute((string Id, int Weight, int Score)[] criteria) =>
        BenchmarkJudgeScoreCalculator.Compute(Rubric([.. criteria.Select(static criterion => (criterion.Id, criterion.Weight))]),
            [.. criteria.Select(static criterion => Score(criterion.Id, criterion.Score))]);

    private static BenchmarkJudgeCriterionScoreV2 Score(string id, int score) =>
        new(id, score, "rationale");

    private static BenchmarkJudgeRubricV1 Rubric(params (string Id, int Weight)[] criteria) =>
        new(BenchmarkJudgePolicyVersions.RubricVersion,
            [.. criteria.Select(static criterion => new BenchmarkJudgeRubricCriterionV1(criterion.Id, $"Title {criterion.Id}", $"Description {criterion.Id}.", criterion.Weight))]);
}
