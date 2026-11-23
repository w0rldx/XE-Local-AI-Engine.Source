namespace XE_Local_AI_Engine.Tests.ModelLab;

using System.Globalization;
using System.Text;
using System.Text.Json;
using XE_Local_AI_Engine.Client.Services.ModelLab;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>The files <c>modellab.py</c> writes, read by the host's parser: a key renamed on either side fails here or in the fixture's own check.</summary>
/// <remarks>The fixture comes from the real Python writers: <c>scripts/tests/test_modellab_contract_fixture.py</c> builds it and fails when the committed copy is stale.</remarks>
[Category(TestCategories.Unit)]
public sealed class ModelLabTraceContractTests
{
    private static readonly Fixture Files = Fixture.Load();

    [Test]
    public void TheFixture_IsThisBuildsContractVersion() =>
        AssertEx.Equal(ModelLabTraceSpec.ContractVersion, Files.ContractVersion);

    [Test]
    public void TheTraceFile_ReadsEveryRecordAndItsFooter_WithANonFiniteNormAsNull()
    {
        var trace = ModelLabTraceSpec.ParseTrace(Bytes(Files.Trace));

        AssertEx.Equal(ModelLabTraceReadStatus.Found, trace.Status);
        AssertEx.Equal("0:0,0:1,1:0,1:1", string.Join(',', trace.Records.Select(static record => string.Create(CultureInfo.InvariantCulture, $"{record.Step}:{record.Layer}"))),
            "Lens and routing lines are not hook records.");
        var first = trace.Records[0];
        AssertEx.Equal("F", first.Kind);
        AssertEx.Equal(expected: 3.5, first.Norm);
        AssertEx.Equal("-1,0,1", Joined(first.Percentiles));
        AssertEx.Equal("7,8", string.Join(',', first.TopIndices));
        AssertEx.Equal("0.5,-0.25", Joined(first.TopValues));
        AssertEx.Null(trace.Records[3].Norm);
        var footer = AssertEx.NotNull(trace.Footer);
        AssertEx.False(footer.Truncated);
        AssertEx.Equal(expected: 4, footer.Records);
        AssertEx.Equal(expected: 0, footer.Dropped);
        AssertEx.Equal(expected: 100_000L, footer.Cap);
    }

    [Test]
    public void TheLensLines_ReadAgainstTheResultsLensSummary()
    {
        var result = AssertEx.NotNull(ModelLabTraceSpec.ReadResultOrNull(Bytes(Files.Result)));

        var lens = ModelLabTraceSpec.ParseLens(Bytes(Files.Trace), AssertEx.NotNull(result.Lens), result.Tokens.Count);

        AssertEx.Equal(ModelLabTraceReadStatus.Found, lens.Status);
        AssertEx.Equal(expected: 2, lens.Records.Count);
        var masked = lens.Records[1];
        AssertEx.Equal(expected: 1, masked.Step);
        AssertEx.Equal("13,12", string.Join(',', masked.Ids[1]));
        AssertEx.Equal("-0.5,-0.9", Joined(masked.Logprobs[1]));
        AssertEx.Equal("-0.3,null", Joined(masked.ChosenLogprobs), "A masked chosen token is null.");
        AssertEx.Equal("1,null", Joined(masked.ChosenRanks.Select(static rank => (double?)rank)), "And so is its rank.");
        AssertEx.Equal("0.2,0.6", Joined(masked.Entropies));
        AssertEx.Equal("é", masked.Texts["13"]);
        AssertEx.Equal(expected: 2, AssertEx.NotNull(lens.Footer).Records);
    }

    [Test]
    public void TheRoutingLines_ReadAgainstTheResultsRoutingSummary_WithTheirWeightsBitExact()
    {
        var result = AssertEx.NotNull(ModelLabTraceSpec.ReadResultOrNull(Bytes(Files.Result)));

        var routing = ModelLabTraceSpec.ParseRouting(Bytes(Files.Trace), AssertEx.NotNull(result.Routing), result.BlockCount, result.Tokens.Count);

        AssertEx.Equal(ModelLabTraceReadStatus.Found, routing.Status);
        AssertEx.Equal("0,1", string.Join(',', routing.Records.Select(static record => record.Step)));
        AssertEx.Equal("3,0|1,2", string.Join('|', routing.Records.Select(static record => string.Join(',', record.Experts[0]))));
        AssertEx.Equal("0.5,0.25|1,0.125",
            string.Join('|', routing.Records.Select(static record => string.Join(',', record.WeightBits[0].Select(static bits => ModelLabTraceSpec.RoutingWeight(bits).ToString(CultureInfo.InvariantCulture))))));
        var footer = AssertEx.NotNull(routing.Footer);
        AssertEx.Equal(expected: 2, footer.Records);
        AssertEx.Equal(expected: 0, footer.Dropped);
    }

    [Test]
    public void TheAttentionFile_ReadsEveryRowAtTheResultsHeadCount()
    {
        var result = AssertEx.NotNull(ModelLabTraceSpec.ReadResultOrNull(Bytes(Files.Result)));

        var attention = ModelLabTraceSpec.ParseAttention(Bytes(Files.Attention), AssertEx.NotNull(result.Attention).Heads);

        AssertEx.Equal(ModelLabTraceReadStatus.Found, attention.Status);
        var second = attention.Records[1];
        AssertEx.Equal(expected: 1, second.Step);
        AssertEx.Equal(expected: 1, second.Layer);
        AssertEx.Equal(expected: 3, second.QueryPosition);
        AssertEx.Equal("1,2", string.Join(',', second.Keys[1]));
        AssertEx.Equal("5000,1234", string.Join(',', second.Weights[1]));
        AssertEx.Equal("1500,3765", string.Join(',', second.Rest));
        AssertEx.Equal(expected: 2, AssertEx.NotNull(attention.Footer).Records);
    }

    /// <summary>The result is read without required members (rows saved by older contracts lack fields), so every field is asserted set.</summary>
    [Test]
    public void TheResult_ReadsEveryFieldTheScriptWrites()
    {
        var result = AssertEx.NotNull(ModelLabTraceSpec.ReadResultOrNull(Bytes(Files.Result)));

        AssertEx.Equal(expected: 3, result.PromptTokens);
        AssertEx.Equal("a b", result.Text);
        AssertEx.Equal(expected: 1.25, result.Seconds);
        AssertEx.Equal(expected: 1000L, result.PeakVramBytes);
        AssertEx.Equal(expected: 2, result.BlockCount);
        AssertEx.Equal("SyntheticForCausalLM|synthetic-device|2.0|5.0|3.14|12.0", string.Join('|', result.ModelClass, result.Device, result.Torch, result.Transformers, result.Python, result.Cuda));
        AssertEx.Equal("0,1", string.Join(',', result.Layers ?? []));
        AssertEx.Equal(ModelLabTraceSpec.Seed, result.Seed);
        AssertEx.Equal(expected: 2, result.MaxNewTokens);
        AssertEx.Equal("11=a|12= b|13=é", string.Join('|', (result.PromptPieces ?? []).Select(static piece => string.Create(CultureInfo.InvariantCulture, $"{piece.Id}={piece.Text}"))));
        AssertEx.Equal("FF", result.LayerKinds);
        AssertEx.Equal("sdpa", result.AttnImplementation);
        AssertEx.Equal(expected: false, result.Warm);
        AssertEx.Equal(expected: 2.5, result.LoadSeconds);

        var token = result.Tokens[1];
        AssertEx.Equal(expected: 12, token.Id);
        AssertEx.Equal(" b", token.Text);
        AssertEx.Equal(expected: -0.3, token.Logprob);
        AssertEx.Equal("12, 13", string.Join(", ", (token.Candidates ?? []).Select(static candidate => candidate.Id)));
        AssertEx.Equal(" b|é", string.Join('|', (token.Candidates ?? []).Select(static candidate => candidate.Text)));
        AssertEx.Equal("-0.3,-1.8", Joined((token.Candidates ?? []).Select(static candidate => (double?)candidate.Logprob)));
        AssertEx.Equal(expected: -0.25, token.ProcessedLogprob);
        AssertEx.Equal(expected: 2, token.ProcessedCandidates?.Count);
        AssertEx.Equal(expected: 2, result.CandidateSteps, "Both steps kept their candidates.");

        var lens = AssertEx.NotNull(result.Lens);
        AssertEx.Equal("0,1|2|2|False|True|16",
            string.Create(CultureInfo.InvariantCulture, $"{string.Join(',', lens.Layers)}|{lens.TopTokens}|{lens.Records}|{lens.Truncated}|{lens.TiedEmbeddings}|{lens.VocabSize}"));
        var routing = AssertEx.NotNull(result.Routing);
        AssertEx.Equal("1|4|2|False|bfloat16-bits|2|False",
            string.Create(CultureInfo.InvariantCulture,
                $"{string.Join(',', routing.Layers)}|{routing.Experts}|{routing.ExpertsPerToken}|{routing.NormalizedTopK}|{routing.WeightEncoding}|{routing.Records}|{routing.Truncated}"));
        var attention = AssertEx.NotNull(result.Attention);
        AssertEx.Equal("1|2|1|8|2|10000|2|False",
            string.Create(CultureInfo.InvariantCulture,
                $"{string.Join(',', attention.Layers)}|{attention.Heads}|{attention.KvHeads}|{attention.HeadDim}|{attention.TopKeys}|{attention.WeightScale}|{attention.Records}|{attention.Truncated}"));
    }

    /// <summary>A row saved under contract 6 (frozen fixture) still reads, its candidates whole and no count of the steps that kept them.</summary>
    [Test]
    public void AContract6Result_StillReads_WithEveryStepsCandidates_AndNoCandidateSteps()
    {
        var saved = Fixture.Load("model-lab-contract-v6.json");
        var result = AssertEx.NotNull(ModelLabTraceSpec.ReadResultOrNull(Bytes(saved.Result)));

        AssertEx.Equal(expected: 6, saved.ContractVersion);
        AssertEx.Null(result.CandidateSteps);
        AssertEx.Equal(expected: 2, result.Tokens.Count);
        AssertEx.True(result.Tokens.All(static token => token.Candidates is { Count: 2 } && token.ProcessedCandidates is { Count: 2 }));
    }

    /// <summary>Rows saved under contracts 6 and 7 (frozen fixtures) still read through every reader: hook records, lens and attention whole, and no routing.</summary>
    [Test]
    [Arguments("model-lab-contract-v6.json", 6)]
    [Arguments("model-lab-contract-v7.json", 7)]
    public void AnOlderContractsFiles_StillReadThroughEveryReader_WithNoRouting(string name, int version)
    {
        var saved = Fixture.Load(name);
        var result = AssertEx.NotNull(ModelLabTraceSpec.ReadResultOrNull(Bytes(saved.Result)));

        AssertEx.Equal(version, saved.ContractVersion);
        AssertEx.Null(result.Routing);
        var trace = ModelLabTraceSpec.ParseTrace(Bytes(saved.Trace));
        AssertEx.Equal(ModelLabTraceReadStatus.Found, trace.Status);
        AssertEx.Equal(expected: 4, trace.Records.Count);
        var lens = ModelLabTraceSpec.ParseLens(Bytes(saved.Trace), AssertEx.NotNull(result.Lens), result.Tokens.Count);
        AssertEx.Equal(ModelLabTraceReadStatus.Found, lens.Status);
        AssertEx.Equal(expected: 2, lens.Records.Count);
        var attention = ModelLabTraceSpec.ParseAttention(Bytes(saved.Attention), AssertEx.NotNull(result.Attention).Heads);
        AssertEx.Equal(ModelLabTraceReadStatus.Found, attention.Status);
        AssertEx.Equal(expected: 2, attention.Records.Count);
    }

    /// <summary>A key the script renames or drops makes the file unreadable rather than reading as a default.</summary>
    [Test]
    [Arguments("trace", "\"ti\":", "\"top\":")]
    [Arguments("lens", "{\"lens\":{\"s\":0,", "{\"lens\":{\"step\":0,")]
    [Arguments("trace", "\"dropped\":0,\"payloadBytes\":309", "\"payloadBytes\":309")]
    [Arguments("attention", "\"q\":", "\"query\":")]
    [Arguments("routing", "\"x\":", "\"experts\":")]
    [Arguments("routing", "{\"routingFooter\":{\"truncated\":false,\"records\":2,\"dropped\":0,", "{\"routingFooter\":{\"truncated\":false,\"records\":2,")]
    public void ARenamedOrMissingRequiredKey_MakesTheFileUnavailable(string file, string key, string replacement)
    {
        var text = file == "attention" ? Files.Attention : Files.Trace;
        AssertEx.Contains(text, key, StringComparison.Ordinal);
        var broken = Bytes(ReplaceFirst(text, key, replacement));
        var result = AssertEx.NotNull(ModelLabTraceSpec.ReadResultOrNull(Bytes(Files.Result)));

        var status = file switch
        {
            "attention" => ModelLabTraceSpec.ParseAttention(broken, AssertEx.NotNull(result.Attention).Heads).Status,
            "lens" => ModelLabTraceSpec.ParseLens(broken, AssertEx.NotNull(result.Lens), result.Tokens.Count).Status,
            "routing" => ModelLabTraceSpec.ParseRouting(broken, AssertEx.NotNull(result.Routing), result.BlockCount, result.Tokens.Count).Status,
            _ => ModelLabTraceSpec.ParseTrace(broken).Status
        };

        AssertEx.Equal(ModelLabTraceReadStatus.Unavailable, status);
    }

    private static string Joined(IEnumerable<double?> values) =>
        string.Join(',', values.Select(static value => value?.ToString(CultureInfo.InvariantCulture) ?? "null"));

    private static byte[] Bytes(string text) =>
        Encoding.UTF8.GetBytes(text);

    private static string ReplaceFirst(string text, string key, string replacement)
    {
        var at = text.IndexOf(key, StringComparison.Ordinal);
        return string.Concat(text.AsSpan(0, at), replacement, text.AsSpan(at + key.Length));
    }

    private sealed class Fixture
    {
        public required int ContractVersion { get; init; }

        public required string Trace { get; init; }

        public required string Attention { get; init; }

        public required string Result { get; init; }

        public static Fixture Load(string name = "model-lab-contract.json")
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)));
            var root = document.RootElement;
            return new Fixture
            {
                ContractVersion = root.GetProperty("contractVersion").GetInt32(),
                Trace = root.GetProperty("trace").GetString()!,
                Attention = root.GetProperty("attention").GetString()!,
                Result = root.GetProperty("result").GetString()!
            };
        }
    }
}
