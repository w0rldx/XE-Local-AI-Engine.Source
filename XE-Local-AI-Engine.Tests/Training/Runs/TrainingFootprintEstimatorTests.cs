namespace XE_Local_AI_Engine.Tests.Training.Runs;

using XE_Local_AI_Engine.Client.Services.Training.BaseArtifacts;
using XE_Local_AI_Engine.Client.Services.Training.Runs;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The estimator's job is not precision — activation memory is inherently model- and kernel-dependent — but
///     direction: it must never under-estimate its way into admitting a run that OOMs an hour in.
/// </summary>
public sealed class TrainingFootprintEstimatorTests
{
    private const long OneGigabyte = 1024L * 1024 * 1024;

    private static readonly BaseCheckpointConfigV1 Llama8B = new()
    {
        Architectures = ["LlamaForCausalLM"],
        HiddenSize = 4096,
        IntermediateSize = 14336,
        NumHiddenLayers = 32,
        NumAttentionHeads = 32,
        VocabSize = 128256,
        MaxPositionEmbeddings = 131072,
        TorchDtype = "bfloat16"
    };

    [Test]
    public void ParameterCount_IsDerivedFromTheWeightShardsAndTheDeclaredDtype()
    {
        IReadOnlyList<BaseArtifactFileView> files =
        [
            new("Weights", "model-00001-of-00002.safetensors", "/base/model-00001-of-00002.safetensors", 8_000_000_000, null),
            new("Weights", "model-00002-of-00002.safetensors", "/base/model-00002-of-00002.safetensors", 8_000_000_000, null),
            // Neither the index nor the tokenizer holds tensors; counting them would inflate the estimate.
            new("Weights", "model.safetensors.index.json", "/base/model.safetensors.index.json", 30_000, null),
            new("Tokenizer", "tokenizer.json", "/base/tokenizer.json", 9_000_000, null),
            new("Config", "config.json", "/base/config.json", 800, null)
        ];

        var parameters = TrainingFootprintEstimator.EstimateParameterCount(files, Llama8B);

        // 16 GB of bf16 shards is an 8B model.
        AssertEx.Equal(expected: 8_000_000_000L, parameters);
    }

    [Test]
    public void ParameterCount_WithNoSafetensorsShards_IsZero()
    {
        IReadOnlyList<BaseArtifactFileView> files = [new("Config", "config.json", "/base/config.json", 800, null)];

        AssertEx.Equal(expected: 0L, TrainingFootprintEstimator.EstimateParameterCount(files, Llama8B));
    }

    [Test]
    public void ParameterCount_WithAnUndeclaredDtype_AssumesTwoBytes()
    {
        IReadOnlyList<BaseArtifactFileView> files = [new("Weights", "model.safetensors", "/base/model.safetensors", 2_000_000_000, null)];

        var undeclared = TrainingFootprintEstimator.EstimateParameterCount(files, Llama8B with
        {
            TorchDtype = null
        });
        var eightBit = TrainingFootprintEstimator.EstimateParameterCount(files, Llama8B with
        {
            TorchDtype = "int8"
        });

        // Assuming the wider dtype over-counts parameters, which over-counts the footprint, which refuses rather
        // than admits — the safe direction for an unknown.
        AssertEx.Equal(expected: 1_000_000_000L, undeclared);
        AssertEx.True(eightBit > undeclared, "A narrower declared dtype means more parameters for the same bytes.");
    }

    [Test]
    public void Estimate_ScalesWithBothActivationLevers()
    {
        var baseline = Options(sequenceLength: 1024, batchSize: 1);
        var longer = TrainingFootprintEstimator.Estimate(8_000_000_000, Llama8B, Options(sequenceLength: 4096, batchSize: 1));
        var wider = TrainingFootprintEstimator.Estimate(8_000_000_000, Llama8B, Options(sequenceLength: 1024, batchSize: 4));
        var smallest = TrainingFootprintEstimator.Estimate(8_000_000_000, Llama8B, baseline);

        AssertEx.True(longer.GpuBytes > smallest.GpuBytes, "A longer sequence must cost more.");
        AssertEx.True(wider.GpuBytes > smallest.GpuBytes, "A larger batch must cost more.");
    }

    [Test]
    public void Estimate_NeverFallsBelowTheQuantizedWeightsFloor()
    {
        // A checkpoint with no declared shape at all: every scaling term collapses to nothing, so only the fail-safe
        // floor is left standing.
        var shapeless = new BaseCheckpointConfigV1();

        var estimate = TrainingFootprintEstimator.Estimate(8_000_000_000, shapeless, Options(sequenceLength: 128, batchSize: 1));

        AssertEx.True(estimate.GpuBytes >= (long)(8_000_000_000L * TrainingFootprintEstimator.QuantizedBytesPerParameter)
            + TrainingFootprintEstimator.CudaContextOverheadBytes,
            "The frozen 4-bit weights and a CUDA context have to be resident no matter what the rest of the formula says.");
    }

    [Test]
    public void Estimate_MarksVeryLargeCheckpointsExperimental()
    {
        var large = TrainingFootprintEstimator.Estimate(TrainingFootprintEstimator.ExperimentalParameterThreshold, Llama8B, Options(1024, 1));
        var ordinary = TrainingFootprintEstimator.Estimate(8_000_000_000, Llama8B, Options(1024, 1));

        AssertEx.True(large.Experimental, "27B and above is beyond what this feature has been exercised on.");
        AssertEx.False(ordinary.Experimental, "An 8B checkpoint is the ordinary case.");
    }

    [Test]
    public void Estimate_OfAnEightBillionParameterModel_LandsInThePublishedRange()
    {
        var estimate = TrainingFootprintEstimator.Estimate(8_000_000_000, Llama8B, Options(sequenceLength: 2048, batchSize: 2));

        // Published unsloth reference points put an 8B QLoRA run near 6 GB. The estimate carries headroom on top, so
        // it should sit above that and comfortably inside a single card — drifting outside either bound would mean
        // the formula has become useless in one direction or the other.
        AssertEx.True(estimate.GpuBytes >= 6 * OneGigabyte, $"The estimate {estimate.GpuBytes} is below the published 8B reference point.");
        AssertEx.True(estimate.GpuBytes <= 16 * OneGigabyte, $"The estimate {estimate.GpuBytes} is implausibly large for an 8B QLoRA run.");
    }

    [Test]
    public void TrainableParameters_GrowWithRankAndDepth()
    {
        var rank16 = TrainingFootprintEstimator.EstimateTrainableParameters(Llama8B, loraRank: 16);
        var rank32 = TrainingFootprintEstimator.EstimateTrainableParameters(Llama8B, loraRank: 32);

        AssertEx.Equal(rank16 * 2, rank32);
        AssertEx.True(rank16 > 0 && rank16 < 8_000_000_000L / 100,
            "The adapter is a small fraction of the base — that is the whole reason QLoRA is cheap.");
        AssertEx.Equal(expected: 0L, TrainingFootprintEstimator.EstimateTrainableParameters(new BaseCheckpointConfigV1(), loraRank: 16));
    }

    [Test]
    public async Task TryReadConfig_ParsesSnakeCaseAndToleratesAPartialDocument()
    {
        var directory = Directory.CreateTempSubdirectory("training-config");
        try
        {
            var path = Path.Combine(directory.FullName, "config.json");
            await File.WriteAllTextAsync(path,
                """{"architectures":["LlamaForCausalLM"],"hidden_size":2048,"num_hidden_layers":16,"torch_dtype":"bfloat16"}""");

            var config = AssertEx.NotNull(TrainingFootprintEstimator.TryReadConfig(path), "A well-formed config must parse.");

            AssertEx.Equal(expected: 2048, config.HiddenSize!.Value);
            AssertEx.Equal(expected: 16, config.NumHiddenLayers!.Value);
            AssertEx.Equal("bfloat16", config.TorchDtype);
            // Omitted fields are absent, not defaulted: a repository is free to leave any of them out.
            AssertEx.Null(config.IntermediateSize, "An omitted field must stay absent.");

            await File.WriteAllTextAsync(path, "{not json");
            AssertEx.Null(TrainingFootprintEstimator.TryReadConfig(path), "A document that will not parse reports absence.");
            AssertEx.Null(TrainingFootprintEstimator.TryReadConfig(Path.Combine(directory.FullName, "missing.json")),
                "A missing config reports absence rather than throwing.");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static TrainingRunOptionsV1 Options(int sequenceLength, int batchSize) =>
        new()
        {
            MaxSeqLength = sequenceLength,
            PerDeviceTrainBatchSize = batchSize,
            LoraR = 16
        };
}
