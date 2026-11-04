namespace XE_Local_AI_Engine.Tests.ModelLab;

using System.Globalization;
using System.Net;
using System.Text;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.ModelLab;
using XE_Local_AI_Engine.Client.Services.Models.Implementation;
using XE_Local_AI_Engine.Client.Services.Training.BaseArtifacts;
using XE_Local_AI_Engine.Client.Services.Training.Runs;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Tests.Providers.HuggingFace;
using XE_Local_AI_Engine.Tests.Providers.Training;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>The fit and pairing refusals, over the real checkpoint store and a scripted Hub. Sizes are the repositories' published ones.</summary>
[Category(TestCategories.Unit)]
public sealed class ModelLabCheckpointPreflightServiceTests : IDisposable
{
    private const string Commit = "2fc06364715b967f1860aea9cf38778875588b17";
    private const string Gguf = "org/Some-GGUF:Q4_K_M";
    private const long Weights08B = 1_746_942_600;
    private const long Weights9B = 19_306_310_880;
    private const long Weights27B = 55_563_006_776;
    private const long Card32Gb = 32_607L * 1024 * 1024;
    private const string Config08B = """{"model_type":"qwen3_5","architectures":["Qwen3_5ForConditionalGeneration"],"text_config":{"hidden_size":1024,"num_hidden_layers":24}}""";
    private const string Config9B = """{"model_type":"qwen3_5","architectures":["Qwen3_5ForConditionalGeneration"],"text_config":{"hidden_size":4096,"num_hidden_layers":32}}""";

    private readonly IRuntimeDeviceAudit _audit = Substitute.For<IRuntimeDeviceAudit>();
    private readonly IGgufMetadataReader _ggufReader = Substitute.For<IGgufMetadataReader>();
    private readonly IGgufModelStore _ggufStore = Substitute.For<IGgufModelStore>();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "xe-lab-preflight-" + Guid.NewGuid().ToString("N"));
    private int _configBytes = 100;
    private int _fileRequests;

    public ModelLabCheckpointPreflightServiceTests()
    {
        _ = _audit.GetEffectiveProfileAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(Profile(Card32Gb));
        _ = _ggufStore.ResolveModelFilePathAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        _ = _ggufStore.ResolveModelFilePathAsync(Gguf, Arg.Any<CancellationToken>()).Returns("/models/some.gguf");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public async Task The27B_IsRefusedBeforeAnyFileIsRequested()
    {
        using var files = Files(Config9B);
        var service = Service(files, Weights27B);

        var result = await service.CheckAsync("Qwen/Qwen3.8-27B", revision: null, Gguf, CancellationToken.None);

        AssertEx.Equal(ModelLabCheckpointRefusal.CannotFitCard, result.Refusal!.Value);
        AssertEx.Contains(result.RefusalMessage!, "cannot fit this card", StringComparison.Ordinal);
        AssertEx.Equal(Weights27B + ModelLabCheckpointPreflightService.WorkingMemoryBytes, result.RequiredVramBytes!.Value);
        AssertEx.Equal(Card32Gb, result.CardVramBytes!.Value);
        AssertEx.Equal(expected: 0, _fileRequests, "A refused checkpoint must not transfer anything, not even its config.");
        await _ggufReader.DidNotReceive().ReadTensorsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task The9B_WithItsOwnGguf_Passes_AndFetchesOnlyTheConfig()
    {
        GgufHeader(embeddingLength: 4096, blockCount: 32);
        using var files = Files(Config9B);

        var result = await Service(files, Weights9B).CheckAsync("Qwen/Qwen3.5-9B", "main", Gguf, CancellationToken.None);

        AssertEx.Null(result.Refusal, result.RefusalMessage);
        AssertEx.Equal(Commit, result.Revision!, "The answer names the commit the branch resolved to.");
        AssertEx.Equal(Weights9B, result.WeightBytes!.Value);
        AssertEx.Equal("apache-2.0", result.License!);
        AssertEx.Equal(expected: 4096, result.CheckpointHiddenSize!.Value);
        AssertEx.Equal(expected: 32, result.CheckpointBlockCount!.Value);
        AssertEx.Equal(expected: 1, _fileRequests, "Only config.json may be transferred before the operator commits to the download.");
    }

    [Test]
    public async Task ADifferentEmbeddingSize_IsRefused()
    {
        GgufHeader(embeddingLength: 4096, blockCount: 32);
        using var files = Files(Config08B);

        var result = await Service(files, Weights08B).CheckAsync("Qwen/Qwen3.5-0.8B", revision: null, Gguf, CancellationToken.None);

        AssertEx.Equal(ModelLabCheckpointRefusal.EmbeddingMismatch, result.Refusal!.Value);
        AssertEx.Contains(result.RefusalMessage!, "1024", StringComparison.Ordinal);
        AssertEx.Contains(result.RefusalMessage!, "4096", StringComparison.Ordinal);
    }

    [Test]
    public async Task ADifferentBlockCount_IsRefused()
    {
        GgufHeader(embeddingLength: 1024, blockCount: 25);
        using var files = Files(Config08B);

        var result = await Service(files, Weights08B).CheckAsync("Qwen/Qwen3.5-0.8B", revision: null, Gguf, CancellationToken.None);

        AssertEx.Equal(ModelLabCheckpointRefusal.BlockCountMismatch, result.Refusal!.Value);
        AssertEx.Contains(result.RefusalMessage!, "MTP head", StringComparison.Ordinal);
    }

    [Test]
    public async Task AGgufWithFewerBlocks_IsRefusedWithoutBlamingMtp()
    {
        GgufHeader(embeddingLength: 1024, blockCount: 23);
        using var files = Files(Config08B);

        var result = await Service(files, Weights08B).CheckAsync("Qwen/Qwen3.5-0.8B", revision: null, Gguf, CancellationToken.None);

        AssertEx.Equal(ModelLabCheckpointRefusal.BlockCountMismatch, result.Refusal!.Value);
        AssertEx.False(result.RefusalMessage!.Contains("MTP", StringComparison.Ordinal));
    }

    [Test]
    public async Task AnUnmeasuredCard_IsRefused()
    {
        _ = _audit.GetEffectiveProfileAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(Profile(vramBytes: null));
        using var files = Files(Config08B);

        var result = await Service(files, Weights08B).CheckAsync("Qwen/Qwen3.5-0.8B", revision: null, Gguf, CancellationToken.None);

        AssertEx.Equal(ModelLabCheckpointRefusal.VramUnknown, result.Refusal!.Value);
        AssertEx.Null(result.CardVramBytes);
    }

    [Test]
    public async Task AGgufThatIsNotInstalled_IsRefused()
    {
        using var files = Files(Config08B);

        var result = await Service(files, Weights08B).CheckAsync("Qwen/Qwen3.5-0.8B", revision: null, "org/Other-GGUF:Q4_K_M", CancellationToken.None);

        AssertEx.Equal(ModelLabCheckpointRefusal.GgufNotInstalled, result.Refusal!.Value);
    }

    [Test]
    public async Task AnUnreadableGgufHeader_IsRefused()
    {
        GgufHeader(embeddingLength: null, blockCount: null, GgufTensorReadFailure.Unreadable);
        using var files = Files(Config08B);

        var result = await Service(files, Weights08B).CheckAsync("Qwen/Qwen3.5-0.8B", revision: null, Gguf, CancellationToken.None);

        AssertEx.Equal(ModelLabCheckpointRefusal.GgufUnreadable, result.Refusal!.Value);
    }

    [Test]
    [Arguments("{not json")]
    [Arguments("""{"model_type":"qwen3_5"}""")]
    public async Task AConfigWithoutAShape_IsRefused(string config)
    {
        GgufHeader(embeddingLength: 1024, blockCount: 24);
        using var files = Files(config);

        var result = await Service(files, Weights08B).CheckAsync("Qwen/Qwen3.5-0.8B", revision: null, Gguf, CancellationToken.None);

        AssertEx.Equal(ModelLabCheckpointRefusal.ConfigUnreadable, result.Refusal!.Value);
    }

    [Test]
    public async Task AConfigTheHubWillNotServe_IsRefused()
    {
        GgufHeader(embeddingLength: 1024, blockCount: 24);
        using var files = new GgufStoreTestInfrastructure.ScriptedHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Forbidden));

        var result = await Service(files, Weights08B).CheckAsync("Qwen/Qwen3.5-0.8B", revision: null, Gguf, CancellationToken.None);

        AssertEx.Equal(ModelLabCheckpointRefusal.ConfigUnreadable, result.Refusal!.Value);
    }

    /// <summary>Counting an unsized shard as nothing would call any checkpoint a fit.</summary>
    [Test]
    public async Task AWeightFileWithoutASize_IsRefusedBeforeAnyFileIsRequested()
    {
        using var files = Files(Config08B);

        var result = await Service(files, weightBytes: 0).CheckAsync("Qwen/Qwen3.5-0.8B", revision: null, Gguf, CancellationToken.None);

        AssertEx.Equal(ModelLabCheckpointRefusal.WeightSizeUnknown, result.Refusal!.Value);
        AssertEx.Equal(expected: 0, _fileRequests);
    }

    [Test]
    [Arguments(0)]
    [Arguments(2 * 1024 * 1024)]
    public async Task AConfigOutsideTheSizeCap_IsNotDownloaded(int listedBytes)
    {
        GgufHeader(embeddingLength: 1024, blockCount: 24);
        using var files = Files(Config08B);

        var result = await Service(files, Weights08B, listedBytes).CheckAsync("Qwen/Qwen3.5-0.8B", revision: null, Gguf, CancellationToken.None);

        AssertEx.Equal(ModelLabCheckpointRefusal.ConfigUnreadable, result.Refusal!.Value);
        AssertEx.Equal(expected: 0, _fileRequests);
    }

    [Test]
    public async Task ARepositoryWithoutSafetensors_IsRefused()
    {
        using var files = Files(Config08B);
        var store = BaseCheckpointStoreTests.CreateStore(_root, files, _ => Ok("""{"id":"unsloth/Qwen3.5-0.8B-GGUF","sha":"abc","siblings":[{"rfilename":"model-Q4_K_M.gguf","size":5}]}"""));
        var service = new ModelLabCheckpointPreflightService(store, _audit, new GgufInspectionService(_ggufStore, _ggufReader));

        var result = await service.CheckAsync("unsloth/Qwen3.5-0.8B-GGUF", revision: null, Gguf, CancellationToken.None);

        AssertEx.Equal(ModelLabCheckpointRefusal.NotTrainable, result.Refusal!.Value);
        AssertEx.Null(result.Revision);
    }

    /// <summary>The script reads <c>model.model</c>, so another layout would download all its weights and then fail every trace.</summary>
    [Test]
    [Arguments("gpt2")]
    [Arguments("llama")]
    public async Task AModelTypeTheScriptDoesNotKnow_IsRefused_FetchingOnlyTheConfig(string modelType)
    {
        GgufHeader(embeddingLength: 1024, blockCount: 24);
        using var files = Files(ConfigOfType(modelType));

        var result = await Service(files, Weights08B).CheckAsync("org/Other", revision: null, Gguf, CancellationToken.None);

        AssertEx.Equal(ModelLabCheckpointRefusal.UnsupportedModelType, result.Refusal!.Value);
        AssertEx.Contains(result.RefusalMessage!, modelType, StringComparison.Ordinal);
        AssertEx.Equal(expected: 1, _fileRequests, "Only config.json was transferred.");
    }

    /// <summary>gpt2's config says <c>n_embd</c>, so the shape check alone would call it unreadable rather than say what is wrong.</summary>
    [Test]
    public async Task AnUnknownModelType_IsNamed_EvenWithNoHiddenSize()
    {
        GgufHeader(embeddingLength: 1024, blockCount: 24);
        using var files = Files("""{"model_type":"gpt2","n_embd":768,"n_layer":12}""");

        var result = await Service(files, Weights08B).CheckAsync("openai/gpt2", revision: null, Gguf, CancellationToken.None);

        AssertEx.Equal(ModelLabCheckpointRefusal.UnsupportedModelType, result.Refusal!.Value);
        AssertEx.Contains(result.RefusalMessage!, "gpt2", StringComparison.Ordinal);
    }

    [Test]
    public async Task AConfigThatNamesNoModelType_IsRefused()
    {
        GgufHeader(embeddingLength: 1024, blockCount: 24);
        using var files = Files("""{"text_config":{"hidden_size":1024,"num_hidden_layers":24}}""");

        var result = await Service(files, Weights08B).CheckAsync("org/Other", revision: null, Gguf, CancellationToken.None);

        AssertEx.Equal(ModelLabCheckpointRefusal.UnsupportedModelType, result.Refusal!.Value);
    }

    [Test]
    public async Task ACheckpointOnDisk_OfAModelTypeTheScriptDoesNotKnow_IsRefused()
    {
        GgufHeader(embeddingLength: 1024, blockCount: 24);
        using var files = Files(Config08B);

        var result = await Service(files, Weights08B).CheckDownloadedAsync(Saved(ConfigOfType("gpt2"), license: null), Gguf, CancellationToken.None);

        AssertEx.Equal(ModelLabCheckpointRefusal.UnsupportedModelType, result.Refusal!.Value);
    }

    [Test]
    [Arguments("qwen3_5")]
    [Arguments("qwen3_5_text")]
    public async Task ACheckpointOnDisk_OfAKnownModelType_Passes(string modelType)
    {
        GgufHeader(embeddingLength: 1024, blockCount: 24);
        using var files = Files(Config08B);

        var result = await Service(files, Weights08B).CheckDownloadedAsync(Saved(ConfigOfType(modelType), new BaseArtifactLicenseView("Qwen/Saved", "apache-2.0", IsGated: false, DateTimeOffset.UnixEpoch)), Gguf, CancellationToken.None);

        AssertEx.Null(result.Refusal, result.RefusalMessage);
        AssertEx.Equal(LicenseGateService.BuildModelLabText("Qwen/Saved", "apache-2.0", isGated: false, metadataPresent: true), result.LicenseConfirmationText!);
    }

    /// <summary>The operator must confirm the same words <c>CreateRowAsync</c> records and hashes for this artifact.</summary>
    [Test]
    public async Task ACheckpointOnDisk_WithNoLicenseMetadata_SaysSo_NotThatItDeclaresNone()
    {
        GgufHeader(embeddingLength: 1024, blockCount: 24);
        using var files = Files(Config08B);

        var result = await Service(files, Weights08B).CheckDownloadedAsync(Saved(Config08B, license: null), Gguf, CancellationToken.None);

        AssertEx.Equal(LicenseGateService.BuildModelLabText("Qwen/Saved", license: null, isGated: false, metadataPresent: false), result.LicenseConfirmationText!);
        AssertEx.Contains(result.LicenseConfirmationText!, "no license metadata", StringComparison.Ordinal);
    }

    private static string ConfigOfType(string modelType) =>
        $$"""{"model_type":"{{modelType}}","hidden_size":1024,"num_hidden_layers":24}""";

    private BaseArtifactView Saved(string config, BaseArtifactLicenseView? license)
    {
        _ = Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "saved-config.json");
        File.WriteAllText(path, config);
        return new BaseArtifactView
        {
            Id = Guid.NewGuid(),
            RepoId = "Qwen/Saved",
            Revision = Commit,
            Status = "Ready",
            TotalBytes = Weights08B,
            Files =
            [
                new BaseArtifactFileView("Config", "config.json", path, 100, null),
                new BaseArtifactFileView("Weights", "model.safetensors", Path.Combine(_root, "model.safetensors"), Weights08B, null)
            ],
            License = license,
            ErrorMessage = null,
            Version = 1,
            CreatedAtUtc = DateTimeOffset.UnixEpoch,
            UpdatedAtUtc = DateTimeOffset.UnixEpoch,
            Progress = null
        };
    }

    private ModelLabCheckpointPreflightService Service(GgufStoreTestInfrastructure.ScriptedHandler files, long weightBytes, int? listedConfigBytes = null)
    {
        var hub = BaseCheckpointStoreTests.CreateStore(_root, files, _ => Ok(string.Create(CultureInfo.InvariantCulture,
            $$$"""{"id":"Qwen/Checkpoint","sha":"{{{Commit}}}","cardData":{"license":"apache-2.0"},"siblings":[{"rfilename":"config.json","size":{{{listedConfigBytes ?? _configBytes}}}},{"rfilename":"model.safetensors","size":{{{weightBytes}}},"lfs":{"size":{{{weightBytes}}},"sha256":"00"}}]}""")));
        return new ModelLabCheckpointPreflightService(hub, _audit, new GgufInspectionService(_ggufStore, _ggufReader));
    }

    private GgufStoreTestInfrastructure.ScriptedHandler Files(string config)
    {
        _configBytes = Encoding.UTF8.GetByteCount(config);
        return new((_, _) =>
        {
            _fileRequests++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(config))
            };
        });
    }

    private static HttpResponseMessage Ok(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body)
        };

    private void GgufHeader(long? embeddingLength, long? blockCount, GgufTensorReadFailure failure = GgufTensorReadFailure.None) =>
        _ggufReader.ReadTensorsAsync("/models/some.gguf", Arg.Any<CancellationToken>())
                   .Returns(new GgufTensorInspection
                   {
                       Failure = failure,
                       Version = 3,
                       Architecture = "qwen35",
                       ModelName = null,
                       ParamCount = null,
                       FileType = null,
                       BlockCount = blockCount,
                       NextnPredictLayers = null,
                       Alignment = null,
                       AttentionHeadCount = null,
                       AttentionHeadCountKv = null,
                       EmbeddingLength = embeddingLength,
                       ContextLength = null,
                       ExpertCount = null,
                       ChatTemplate = null,
                       SplitCount = null,
                       TensorCount = 0,
                       Tensors = [],
                       HeaderBytes = 0,
                       FileSizeBytes = 0
                   });

    private static HardwareProfile Profile(long? vramBytes) =>
        new()
        {
            TotalRamBytes = 1,
            AvailableRamBytes = 1,
            VramBytes = vramBytes,
            VramKnown = vramBytes is not null,
            GpuVendor = GpuVendor.Nvidia,
            GpuAccelAvailable = vramBytes is not null,
            CpuCores = 1,
            FreeDiskBytes = 1
        };
}
