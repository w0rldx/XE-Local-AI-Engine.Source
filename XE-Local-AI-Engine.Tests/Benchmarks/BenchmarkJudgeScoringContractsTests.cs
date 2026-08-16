namespace XE_Local_AI_Engine.Tests.Benchmarks;

using System.Text.Json;
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

    [Test]
    public void Prompt_OmitsReferenceAnswerWhenNullAndEmbedsRawJson()
    {
        var rubric = Rubric(("alpha", 1));
        const string taskJson = "\"Write a haiku.\"";
        const string outputParts = """[{"kind":"text","text":"hello"}]""";

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
        AssertEx.Equal("hello", bare.RootElement.GetProperty("primaryOutputParts")[0].GetProperty("text").GetString());
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
    }

    private static int Compute((string Id, int Weight, int Score)[] criteria) =>
        BenchmarkJudgeScoreCalculator.Compute(Rubric([.. criteria.Select(static criterion => (criterion.Id, criterion.Weight))]),
            [.. criteria.Select(static criterion => Score(criterion.Id, criterion.Score))]);

    private static BenchmarkJudgeCriterionScoreV2 Score(string id, int score) => new(id, score, "rationale");

    private static BenchmarkJudgeRubricV1 Rubric(params (string Id, int Weight)[] criteria) =>
        new(BenchmarkJudgePolicyVersions.RubricVersion,
            [.. criteria.Select(static criterion => new BenchmarkJudgeRubricCriterionV1(criterion.Id, $"Title {criterion.Id}", $"Description {criterion.Id}.", criterion.Weight))]);
}
