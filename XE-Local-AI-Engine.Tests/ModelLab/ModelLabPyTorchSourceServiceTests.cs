namespace XE_Local_AI_Engine.Tests.ModelLab;

using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Endpoints.ModelLab.V1;
using XE_Local_AI_Engine.Client.Services.ModelLab;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.HuggingFace.Contracts;
using XE_Local_AI_Engine.Providers.Training.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The PyTorch source index on the node's real DI: real GPU gate, supervisor, ledger, artifact store and data directory. Only the
///     script, the runtime's install state and venv, the Hub and the GGUF header are scripted; no test runs Python.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class ModelLabPyTorchSourceServiceTests
{
    private const string ModelingFile = "models/qwen3_5/modeling_qwen3_5.py";
    private const string TorchFile = "torch:nn/modules/linear.py";
    private const string HubCommit = "0123456789abcdef0123456789abcdef01234567";

    /// <summary>From <c>site-packages/transformers</c> up to the fake runtime's root.</summary>
    private const string EscapingFile = "../../../../../../evil.py";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters =
        {
            new JsonStringEnumConverter()
        }
    };

    [Test]
    public async Task ACheckpointsConfig_BuildsAnIndex_WithNoGpuHold_AndLeavesNoWorkDirectory()
    {
        using var runtime = new FakeRuntime();
        var script = new ScriptedSourceJob(runtime)
        {
            Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        await using var lab = await Lab.StartAsync(runtime, script);

        var building = lab.Service.BuildAsync(lab.CheckpointRequest(), CancellationToken.None);
        await script.Started.Task.WaitAsync(TestBudgets.Contended);

        AssertEx.Null(lab.Host.Gate.ExclusiveKind, "A source job loads no weights, so it holds no GPU gate.");
        AssertEx.False(lab.Host.Supervisor.IsKeepWarmSuppressed(), "Nor the runtime-mutation lease: a loaded model must not refuse it.");
        AssertEx.Equal(expected: 0L, lab.Host.Ledger.Reserved.GpuBytes);
        using (var job = JsonDocument.Parse(script.LastJob!))
        {
            AssertEx.Equal("source", job.RootElement.GetProperty("kind").GetString()!);
            AssertEx.Equal(expected: 3, job.RootElement.GetProperty("contractVersion").GetInt32());
            AssertEx.Equal(ModelLabTraceSpec.MaxResultBytes, job.RootElement.GetProperty("maxResultBytes").GetInt32());
        }

        AssertEx.Equal("config.json", string.Join(',', script.LastConfigFiles), "The config directory holds the config alone.");
        AssertEx.Equal(ModelLabExperimentServiceTests.LabHost.Config, script.LastConfig!);

        script.Hold.SetResult();
        var outcome = await building;

        var index = AssertEx.NotNull(outcome.Index, outcome.RefusalMessage ?? "built");
        AssertEx.Equal("checkpoint", index.Source.Kind);
        AssertEx.Equal("Qwen/Qwen3.5-0.8B", index.Source.RepoId);
        AssertEx.Equal(lab.ArtifactId, index.Source.BaseArtifactId!.Value);
        AssertEx.Equal($"{Sha256(ModelLabExperimentServiceTests.LabHost.Config)}-5.17.0-2.13.0+cpu-3", index.IndexId);
        AssertEx.Equal("Qwen3_5ForCausalLM", index.Index.ModelClass);
        AssertEx.Null(index.Index.DeclaredArchitectures, "A config with no architectures declares none.");
        AssertEx.Empty(Directory.EnumerateFileSystemEntries(lab.WorkRoot), "No work directory survives the job.");
        AssertEx.Equal(index.IndexId, (await lab.Service.GetLastAsync(ModelLabExperimentServiceTests.Gguf, CancellationToken.None)).IndexId);
    }

    /// <summary>B3: the same config.json, from either source, is the same index; the second answer runs no script.</summary>
    [Test]
    public async Task TheSameConfigFromHuggingFace_IsACacheHit_AndKeepsTheConfigUnderItsCommit()
    {
        using var runtime = new FakeRuntime();
        var script = new ScriptedSourceJob(runtime);
        await using var lab = await Lab.StartAsync(runtime, script);
        lab.ServeHubConfig(ModelLabExperimentServiceTests.LabHost.Config);

        var fromCheckpoint = await lab.Service.BuildAsync(lab.CheckpointRequest(), CancellationToken.None);
        var fromHub = await lab.Service.BuildAsync(Lab.HubRequest(), CancellationToken.None);

        AssertEx.Equal(expected: 1, script.SpawnCount, "The second build is answered from the cache.");
        var hub = AssertEx.NotNull(fromHub.Index, fromHub.RefusalMessage ?? "built");
        AssertEx.Equal(fromCheckpoint.Index!.IndexId, hub.IndexId);
        AssertEx.Equal("huggingface", hub.Source.Kind);
        AssertEx.Equal(HubCommit, hub.Source.Revision);
        AssertEx.Null(hub.Source.BaseArtifactId);
        var kept = Path.Combine(ModelLabPyTorchSourceService.IndexRoot(lab.DataDirectory), "configs", Sha256($"Qwen/Qwen3.5-0.8B@{HubCommit}"), "config.json");
        AssertEx.Equal(ModelLabExperimentServiceTests.LabHost.Config, await File.ReadAllTextAsync(kept));
        AssertEx.Equal("huggingface", (await lab.Service.GetLastAsync(ModelLabExperimentServiceTests.Gguf, CancellationToken.None)).Source.Kind);
        AssertEx.Empty(Directory.EnumerateFileSystemEntries(lab.WorkRoot), "The fetch's staging directory goes too.");
    }

    /// <summary>F2: the node reads the config's architectures itself, so an encoder shown through a causal-LM class can be labelled.</summary>
    [Test]
    public async Task TheConfigsArchitectures_AreKeptWithTheIndex_AndServedByTheLastIndex()
    {
        using var runtime = new FakeRuntime();
        var script = new ScriptedSourceJob(runtime);
        await using var lab = await Lab.StartAsync(runtime, script);
        lab.ServeHubConfig("""{"model_type":"xlm-roberta","architectures":["XLMRobertaForSequenceClassification",7]}""");

        var built = await lab.Service.BuildAsync(Lab.HubRequest(), CancellationToken.None);

        var index = AssertEx.NotNull(built.Index, built.RefusalMessage ?? "built");
        AssertEx.Equal("XLMRobertaForSequenceClassification", string.Join(',', index.Index.DeclaredArchitectures ?? []), "Only the strings are kept.");
        var last = await lab.Service.GetLastAsync(ModelLabExperimentServiceTests.Gguf, CancellationToken.None);
        AssertEx.Equal("XLMRobertaForSequenceClassification", string.Join(',', last.Index.DeclaredArchitectures ?? []), "The stored index carries them.");
    }

    [Test]
    public async Task ARuntimeVersionChange_MissesTheCache_AndHidesTheLastIndex()
    {
        using var runtime = new FakeRuntime();
        var script = new ScriptedSourceJob(runtime);
        await using var lab = await Lab.StartAsync(runtime, script);
        var first = await lab.Service.BuildAsync(lab.CheckpointRequest(), CancellationToken.None);

        runtime.SetTorchVersion("2.14.0+cpu");

        _ = await AssertEx.ThrowsAsync<ModelLabPyTorchIndexNotFoundException>(() => lab.Service.GetLastAsync(ModelLabExperimentServiceTests.Gguf, CancellationToken.None));
        var second = await lab.Service.BuildAsync(lab.CheckpointRequest(), CancellationToken.None);
        AssertEx.Equal(expected: 2, script.SpawnCount);
        AssertEx.True(second.Index!.IndexId.EndsWith("-2.14.0+cpu-3", StringComparison.Ordinal), second.Index.IndexId);
        AssertEx.NotEqual(first.Index!.IndexId, second.Index.IndexId);
    }

    [Test]
    public async Task EveryRefusalBeforeTheJob_StartsNoScript()
    {
        using var runtime = new FakeRuntime();
        var script = new ScriptedSourceJob(runtime);
        await using var lab = await Lab.StartAsync(runtime, script);
        var downloading = await lab.Host.SeedDownloadingArtifactAsync();

        await lab.ExpectAsync(ModelLabPyTorchRefusal.CheckpointNotFound, lab.CheckpointRequest() with
        {
            BaseArtifactId = Guid.NewGuid()
        });
        await lab.ExpectAsync(ModelLabPyTorchRefusal.CheckpointNotReady, lab.CheckpointRequest() with
        {
            BaseArtifactId = downloading
        });
        await File.WriteAllTextAsync(lab.ArtifactConfigPath, "not json");
        await lab.ExpectAsync(ModelLabPyTorchRefusal.ConfigUnreadable, lab.CheckpointRequest());
        await File.WriteAllTextAsync(lab.ArtifactConfigPath, "[1]");
        await lab.ExpectAsync(ModelLabPyTorchRefusal.ConfigUnreadable, lab.CheckpointRequest());
        await File.WriteAllTextAsync(lab.ArtifactConfigPath, """{"model_type":"nomic_bert","auto_map":{"AutoModel":"x.Y"}}""");
        await lab.ExpectAsync(ModelLabPyTorchRefusal.RemoteCode, lab.CheckpointRequest());
        await File.WriteAllTextAsync(lab.ArtifactConfigPath, """{"model_type":"x","text_config":{"auto_map":{"AutoModel":"x.Y"}}}""");
        await lab.ExpectAsync(ModelLabPyTorchRefusal.RemoteCode, lab.CheckpointRequest());
        await File.WriteAllBytesAsync(lab.ArtifactConfigPath, new byte[BaseCheckpointConfigException.MaxConfigBytes + 1]);
        await lab.ExpectAsync(ModelLabPyTorchRefusal.ConfigTooLarge, lab.CheckpointRequest());
        File.Delete(lab.ArtifactConfigPath);
        await lab.ExpectAsync(ModelLabPyTorchRefusal.ConfigMissing, lab.CheckpointRequest());

        lab.FailHubConfig(new BaseCheckpointConfigException(BaseCheckpointConfigFailure.ConfigTooLarge, "too large"));
        await lab.ExpectAsync(ModelLabPyTorchRefusal.ConfigTooLarge, Lab.HubRequest());
        lab.FailHubConfig(new BaseCheckpointConfigException(BaseCheckpointConfigFailure.ConfigMissing, "missing"));
        await lab.ExpectAsync(ModelLabPyTorchRefusal.ConfigMissing, Lab.HubRequest());
        lab.FailHubConfig(new BaseCheckpointConfigException(BaseCheckpointConfigFailure.Unreachable, "unreachable"));
        await lab.ExpectAsync(ModelLabPyTorchRefusal.RepoUnreachable, Lab.HubRequest());
        lab.FailHubConfig(new HuggingFaceDownloadException(HuggingFaceDownloadFailure.Gated, "gated"));
        await lab.ExpectAsync(ModelLabPyTorchRefusal.RepoGated, Lab.HubRequest());
        lab.FailHubConfig(new HuggingFaceDownloadException(HuggingFaceDownloadFailure.Unauthorized, "denied"));
        await lab.ExpectAsync(ModelLabPyTorchRefusal.RepoGated, Lab.HubRequest());
        lab.FailHubConfig(new HuggingFaceDownloadException(HuggingFaceDownloadFailure.Network, "offline"));
        await lab.ExpectAsync(ModelLabPyTorchRefusal.RepoUnreachable, Lab.HubRequest());

        AssertEx.Equal(expected: 0, script.SpawnCount);
        AssertEx.Empty(Directory.EnumerateFileSystemEntries(lab.WorkRoot));
    }

    [Test]
    public async Task ARuntimeThatIsNotReady_OrNotAVenv_OrAnotherPlatform_IsRefused()
    {
        using var runtime = new FakeRuntime();
        var script = new ScriptedSourceJob(runtime);
        await using var lab = await Lab.StartAsync(runtime, script);

        lab.Service.PlatformProbe = static () => false;
        await lab.ExpectAsync(ModelLabPyTorchRefusal.UnsupportedPlatform, lab.CheckpointRequest());
        lab.Service.PlatformProbe = static () => true;

        File.Delete(Path.Combine(runtime.Venv, "pyvenv.cfg"));
        await lab.ExpectAsync(ModelLabPyTorchRefusal.RuntimeUnavailable, lab.CheckpointRequest());
        await File.WriteAllTextAsync(Path.Combine(runtime.Venv, "pyvenv.cfg"), "home = /usr/bin\n");

        Directory.Delete(Path.Combine(runtime.SitePackages, "transformers-5.17.0.dist-info"));
        await lab.ExpectAsync(ModelLabPyTorchRefusal.RuntimeUnavailable, lab.CheckpointRequest());
        _ = Directory.CreateDirectory(Path.Combine(runtime.SitePackages, "transformers-5.17.0.dist-info"));

        lab.Host.Runtime.GetStatus().Returns(Status(TrainingRuntimePhase.InstallingPackages));
        await lab.ExpectAsync(ModelLabPyTorchRefusal.RuntimeUnavailable, lab.CheckpointRequest());

        AssertEx.Equal(expected: 0, script.SpawnCount);
    }

    [Test]
    public async Task ASecondBuildWhileOneRuns_IsBusy()
    {
        using var runtime = new FakeRuntime();
        var script = new ScriptedSourceJob(runtime)
        {
            Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        await using var lab = await Lab.StartAsync(runtime, script);
        lab.ServeHubConfig("""{"model_type":"llama"}""");

        var first = lab.Service.BuildAsync(lab.CheckpointRequest(), CancellationToken.None);
        await script.Started.Task.WaitAsync(TestBudgets.Contended);
        await lab.ExpectAsync(ModelLabPyTorchRefusal.Busy, Lab.HubRequest());
        script.Hold.SetResult();

        AssertEx.NotNull((await first).Index);
        AssertEx.NotNull((await lab.Service.BuildAsync(Lab.HubRequest(), CancellationToken.None)).Index, "The flag comes back with the job.");
    }

    /// <summary>The script's refusal categories reach the API as their stable codes; anything else is a failed build.</summary>
    [Test]
    [Arguments("remote_code", ModelLabPyTorchRefusal.RemoteCode)]
    [Arguments("unsupported_model_type", ModelLabPyTorchRefusal.UnsupportedModelType)]
    [Arguments("config_unreadable", ModelLabPyTorchRefusal.ConfigUnreadable)]
    [Arguments("no_blocks", ModelLabPyTorchRefusal.NoBlocks)]
    [Arguments("result_too_large", ModelLabPyTorchRefusal.ResultTooLarge)]
    [Arguments("internal", ModelLabPyTorchRefusal.IndexFailed)]
    public async Task AScriptError_MapsToItsRefusal_AndCachesNothing(string category, ModelLabPyTorchRefusal expected)
    {
        using var runtime = new FakeRuntime();
        var script = new ScriptedSourceJob(runtime)
        {
            Result = static () => null,
            Lines =
            [
                ModelLabExperimentServiceTests.Handshake,
                $$"""{"event":"error","category":"{{category}}","message":"the script's own words"}""",
                """{"event":"done","cancelled":false}"""
            ],
            ExitCode = 1
        };
        await using var lab = await Lab.StartAsync(runtime, script);

        var outcome = await lab.ExpectAsync(expected, lab.CheckpointRequest());

        AssertEx.Contains(outcome.RefusalMessage, "the script's own words");
        _ = await AssertEx.ThrowsAsync<ModelLabPyTorchIndexNotFoundException>(() => lab.Service.GetLastAsync(ModelLabExperimentServiceTests.Gguf, CancellationToken.None));
        AssertEx.Empty(Directory.EnumerateFileSystemEntries(lab.WorkRoot));
    }

    [Test]
    public async Task AResultOverTheCap_OrUnreadable_OrFromAnotherRuntime_IsNotKept()
    {
        using var runtime = new FakeRuntime();
        var script = new ScriptedSourceJob(runtime)
        {
            Result = static () => new string('x', ModelLabTraceSpec.MaxResultBytes + 1)
        };
        await using var lab = await Lab.StartAsync(runtime, script);

        await lab.ExpectAsync(ModelLabPyTorchRefusal.ResultTooLarge, lab.CheckpointRequest());
        script.Result = static () => "{ not json";
        await lab.ExpectAsync(ModelLabPyTorchRefusal.IndexFailed, lab.CheckpointRequest());
        script.Result = () => script.ResultJson(torchVersion: "2.12.0+cpu");
        await lab.ExpectAsync(ModelLabPyTorchRefusal.IndexFailed, lab.CheckpointRequest());
        script.Result = static () => null;
        await lab.ExpectAsync(ModelLabPyTorchRefusal.IndexFailed, lab.CheckpointRequest());

        var cache = ModelLabPyTorchSourceService.IndexRoot(lab.DataDirectory);
        AssertEx.False(Directory.Exists(cache) && Directory.EnumerateFiles(cache, "*.json").Any(), "No refused result reaches the cache.");
    }

    [Test]
    public async Task AListedFile_IsServedFromTheActiveRuntime_TransformersAndTorch()
    {
        using var runtime = new FakeRuntime();
        var script = new ScriptedSourceJob(runtime);
        await using var lab = await Lab.StartAsync(runtime, script);
        var indexId = (await lab.Service.BuildAsync(lab.CheckpointRequest(), CancellationToken.None)).Index!.IndexId;

        var modeling = await lab.Service.ReadSourceAsync(indexId, ModelingFile, CancellationToken.None);
        var torch = await lab.Service.ReadSourceAsync(indexId, TorchFile, CancellationToken.None);

        var served = AssertEx.NotNull(modeling.Source, modeling.RefusalMessage ?? "served");
        AssertEx.Equal(FakeRuntime.ModelingSource, served.Text);
        AssertEx.Equal(Sha256(FakeRuntime.ModelingSource), served.Sha256);
        AssertEx.Equal(FakeRuntime.LinearSource, AssertEx.NotNull(torch.Source, torch.RefusalMessage ?? "served").Text);
    }

    [Test]
    public async Task AnyOtherFile_IsRefused_EvenInsideThePackage_AndAChangedFileIsNotServed()
    {
        using var runtime = new FakeRuntime();
        var outside = Path.Combine(runtime.Root, "evil.py");
        await File.WriteAllTextAsync(outside, "print('outside')\n");
        var big = runtime.Write("big.py", new string('#', ModelLabPyTorchSourceService.MaxSourceBytes + 1));
        var script = new ScriptedSourceJob(runtime)
        {
            ExtraFiles =
            [
                new ModelLabPyTorchFile
                {
                    File = EscapingFile,
                    Sha256 = Sha256("print('outside')\n")
                },
                new ModelLabPyTorchFile
                {
                    File = "big.py",
                    Sha256 = big
                }
            ]
        };
        await using var lab = await Lab.StartAsync(runtime, script);
        var indexId = (await lab.Service.BuildAsync(lab.CheckpointRequest(), CancellationToken.None)).Index!.IndexId;
        _ = runtime.Write("unlisted.py", "x = 1\n");

        await lab.ExpectSourceAsync(ModelLabPyTorchRefusal.SourceNotInIndex, indexId, "unlisted.py");
        AssertEx.True(File.Exists(Path.GetFullPath(Path.Combine(runtime.SitePackages, "transformers", EscapingFile))), "The escaping name points at a real file with the listed hash.");
        await lab.ExpectSourceAsync(ModelLabPyTorchRefusal.SourceNotInIndex, indexId, EscapingFile);
        await lab.ExpectSourceAsync(ModelLabPyTorchRefusal.SourceNotInIndex, "../" + indexId, ModelingFile);
        await lab.ExpectSourceAsync(ModelLabPyTorchRefusal.SourceNotInIndex, new string('a', 64) + "-5.17.0-2.13.0+cpu-3", ModelingFile);
        await lab.ExpectSourceAsync(ModelLabPyTorchRefusal.SourceTooLarge, indexId, "big.py");

        runtime.SetTorchVersion("2.14.0+cpu");
        await lab.ExpectSourceAsync(ModelLabPyTorchRefusal.SourceChanged, indexId, TorchFile);
        runtime.SetTorchVersion("2.13.0+cpu");

        await File.AppendAllTextAsync(Path.Combine(runtime.SitePackages, "transformers", ModelingFile), "# edited\n");
        await lab.ExpectSourceAsync(ModelLabPyTorchRefusal.SourceChanged, indexId, ModelingFile);
    }

    /// <summary>A changed file leaves the cache key as it was: the refusal drops the stale index, so building again runs the job.</summary>
    [Test]
    public async Task AChangedListedFile_DropsTheCachedIndex_SoTheNextBuildRunsTheJobAgain()
    {
        using var runtime = new FakeRuntime();
        var script = new ScriptedSourceJob(runtime);
        await using var lab = await Lab.StartAsync(runtime, script);
        var first = (await lab.Service.BuildAsync(lab.CheckpointRequest(), CancellationToken.None)).Index!;

        const string edited = FakeRuntime.ModelingSource + "# edited\n";
        var editedHash = runtime.Write(ModelingFile, edited);
        await lab.ExpectSourceAsync(ModelLabPyTorchRefusal.SourceChanged, first.IndexId, ModelingFile);

        var stale = script.ResultJson();
        script.Result = () => stale.Replace(Sha256(FakeRuntime.ModelingSource), editedHash, StringComparison.Ordinal);
        var second = await lab.Service.BuildAsync(lab.CheckpointRequest(), CancellationToken.None);

        AssertEx.Equal(expected: 2, script.SpawnCount, "The rebuild ran the job instead of serving the stale index.");
        var rebuilt = AssertEx.NotNull(second.Index, second.RefusalMessage ?? "built");
        AssertEx.Equal(first.IndexId, rebuilt.IndexId, "The same config on the same runtime keeps its cache key.");
        AssertEx.Equal(edited, AssertEx.NotNull((await lab.Service.ReadSourceAsync(rebuilt.IndexId, ModelingFile, CancellationToken.None)).Source, "served").Text);
    }

    /// <summary>B4 shape on the wire: the 27B fixture joined to a 27B-like GGUF over HTTP, then read back and one of its files served.</summary>
    [Test]
    public async Task OverHttp_The27BFixture_IsJoined_ReadBack_AndItsFilesServed()
    {
        using var runtime = new FakeRuntime();
        var fixture = ModelLabPyTorchJoinerTests.Qwen27BIndex();
        var script = new ScriptedSourceJob(runtime)
        {
            Result = () => JsonSerializer.Serialize(fixture with
            {
                TransformersVersion = FakeRuntime.TransformersVersion,
                TorchVersion = runtime.TorchVersion,
                Files =
                [
                    .. fixture.Files.Select(file => file.File == ModelingFile
                        ? file with
                        {
                            Sha256 = Sha256(FakeRuntime.ModelingSource)
                        }
                        : file)
                ]
            }, JsonSerializerOptions.Web)
        };
        await using var lab = await Lab.StartAsync(runtime, script, Qwen35TensorFixture.Qwen35_27BLike());
        using var client = lab.Host.Factory.CreateClient();
        var route = "/api/local/v1/model-lab/architecture/" + Uri.EscapeDataString(ModelLabExperimentServiceTests.Gguf) + "/pytorch";

        using var missing = lab.Host.Authorized(HttpMethod.Get, route);
        AssertEx.Equal(HttpStatusCode.NotFound, (await client.SendAsync(missing)).StatusCode, "Nothing built yet.");

        using var post = lab.Host.Authorized(HttpMethod.Post, route);
        post.Content = JsonContent.Create(new
        {
            source = "checkpoint",
            baseArtifactId = lab.ArtifactId
        });
        using var built = await client.SendAsync(post);
        AssertEx.Equal(HttpStatusCode.OK, built.StatusCode, await built.Content.ReadAsStringAsync());
        var body = AssertEx.NotNull(await built.Content.ReadFromJsonAsync<ModelLabPyTorchIndexResponse>(JsonOptions));
        AssertEx.True(body.Join.BlocksJoined, body.Join.Reason ?? "joined");
        AssertEx.Equal(expected: 64, body.Join.PyTorchBlockCount);
        AssertEx.Contains(body.Join.Components, static link => link.ComponentId == "blk.63.attention" && link.ModulePaths.Single() == "model.layers.63.self_attn");

        using var get = lab.Host.Authorized(HttpMethod.Get, route);
        using var readBack = await client.SendAsync(get);
        AssertEx.Equal(HttpStatusCode.OK, readBack.StatusCode);
        AssertEx.Equal(body.IndexId, (await readBack.Content.ReadFromJsonAsync<ModelLabPyTorchIndexResponse>(JsonOptions))!.IndexId);

        using var source = lab.Host.Authorized(HttpMethod.Get,
            $"/api/local/v1/model-lab/pytorch-source?index={Uri.EscapeDataString(body.IndexId)}&file={Uri.EscapeDataString(ModelingFile)}");
        using var served = await client.SendAsync(source);
        AssertEx.Equal(HttpStatusCode.OK, served.StatusCode, await served.Content.ReadAsStringAsync());
        AssertEx.Equal(FakeRuntime.ModelingSource, (await served.Content.ReadFromJsonAsync<ModelLabPyTorchSourceFile>(JsonOptions))!.Text);

        using var refused = lab.Host.Authorized(HttpMethod.Get,
            $"/api/local/v1/model-lab/pytorch-source?index={Uri.EscapeDataString(body.IndexId)}&file={Uri.EscapeDataString("torch:nn/modules/conv.py")}");
        using var changed = await client.SendAsync(refused);
        AssertEx.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        var blocked = AssertEx.NotNull(await changed.Content.ReadFromJsonAsync<ModelLabPyTorchBlockedResponse>(JsonOptions));
        AssertEx.Equal(ModelLabPyTorchRefusal.SourceChanged, blocked.Reason, "The fixture's hash names a file this runtime does not hold.");
    }

    [Test]
    public async Task OverHttp_ARefusal_IsA409WithItsCode_AndAnUnknownModelIsA404()
    {
        using var runtime = new FakeRuntime();
        await using var lab = await Lab.StartAsync(runtime, new ScriptedSourceJob(runtime));
        await File.WriteAllTextAsync(lab.ArtifactConfigPath, """{"auto_map":{}}""");
        using var client = lab.Host.Factory.CreateClient();

        using var post = lab.Host.Authorized(HttpMethod.Post, "/api/local/v1/model-lab/architecture/" + Uri.EscapeDataString(ModelLabExperimentServiceTests.Gguf) + "/pytorch");
        post.Content = JsonContent.Create(new
        {
            source = "checkpoint",
            baseArtifactId = lab.ArtifactId
        });
        using var refused = await client.SendAsync(post);
        using var unknown = lab.Host.Authorized(HttpMethod.Post, "/api/local/v1/model-lab/architecture/" + Uri.EscapeDataString("org/Unknown-GGUF:Q4_K_M") + "/pytorch");
        unknown.Content = JsonContent.Create(new
        {
            source = "checkpoint",
            baseArtifactId = lab.ArtifactId
        });
        using var notFound = await client.SendAsync(unknown);

        AssertEx.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var body = AssertEx.NotNull(await refused.Content.ReadFromJsonAsync<ModelLabPyTorchBlockedResponse>(JsonOptions));
        AssertEx.Equal(ModelLabPyTorchRefusal.RemoteCode, body.Reason);
        AssertEx.NotEmpty(body.Message);
        AssertEx.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
    }

    private static TrainingRuntimeStatus Status(TrainingRuntimePhase phase) =>
        new()
        {
            Phase = phase,
            IsRunning = false,
            Terminal = true,
            LogLines = [],
            LogStartSequence = 0,
            SanitizedError = null,
            Installed = null,
            StartedAtUtc = null,
            CompletedAtUtc = null
        };

    private static string Sha256(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>The lab host with this feature's script, a venv laid out on disk, and a Hub config seam.</summary>
    private sealed class Lab : IAsyncDisposable
    {
        private readonly ModelLabExperimentServiceTests.ScriptedProcess _unused;

        private Lab(ModelLabExperimentServiceTests.LabHost host, ModelLabExperimentServiceTests.ScriptedProcess unused)
        {
            Host = host;
            _unused = unused;
        }

        public ModelLabExperimentServiceTests.LabHost Host { get; }

        public ModelLabPyTorchSourceService Service => Host.Services.GetRequiredService<ModelLabPyTorchSourceService>();

        public INodeDataDirectory DataDirectory => Host.Services.GetRequiredService<INodeDataDirectory>();

        public string WorkRoot
        {
            get
            {
                var root = ModelLabExperimentService.WorkRoot(DataDirectory);
                _ = Directory.CreateDirectory(root);
                return root;
            }
        }

        public Guid ArtifactId => Host.Request().BaseArtifactId;

        public string ArtifactConfigPath => Path.Combine(Host.DataRoot, "training", "base", ArtifactId.ToString(), "config.json");

        public static async Task<Lab> StartAsync(FakeRuntime runtime, ScriptedSourceJob script, GgufTensorInspection? gguf = null)
        {
#pragma warning disable CA2000 // The lab owns it.
            var unused = new ModelLabExperimentServiceTests.ScriptedProcess();
#pragma warning restore CA2000
            var host = await ModelLabExperimentServiceTests.LabHost.StartAsync(unused, configure: services =>
            {
                services.RemoveAll<ITrainingProcessSpawner>();
                services.AddSingleton<ITrainingProcessSpawner>(script);
                if (gguf is not null)
                {
                    var reader = Substitute.For<IGgufMetadataReader>();
                    reader.ReadTensorsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(gguf);
                    services.RemoveAll<IGgufMetadataReader>();
                    services.AddSingleton(reader);
                }
            });
            host.Runtime.ResolveInterpreterPath().Returns(runtime.Interpreter);
            var lab = new Lab(host, unused);
            lab.Service.PlatformProbe = static () => true;
            return lab;
        }

        public ModelLabPyTorchBuildRequest CheckpointRequest() =>
            new()
            {
                ModelName = ModelLabExperimentServiceTests.Gguf,
                Source = "checkpoint",
                BaseArtifactId = ArtifactId
            };

        public static ModelLabPyTorchBuildRequest HubRequest() =>
            new()
            {
                ModelName = ModelLabExperimentServiceTests.Gguf,
                Source = "huggingface",
                RepoId = "Qwen/Qwen3.5-0.8B",
                Revision = "main"
            };

        public void ServeHubConfig(string config) =>
            Host.Checkpoints.FetchConfigAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(async call =>
                {
                    var directory = call.ArgAt<string>(2);
                    _ = Directory.CreateDirectory(directory);
                    var path = Path.Combine(directory, "config.json");
                    await File.WriteAllTextAsync(path, config);
                    return new BaseCheckpointManifest
                    {
                        RepoId = call.ArgAt<string>(0),
                        Revision = HubCommit,
                        Files =
                        [
                            new BaseCheckpointFile
                            {
                                Role = BaseCheckpointFileRole.Config,
                                FileName = "config.json",
                                SizeBytes = config.Length,
                                LocalPath = path
                            }
                        ],
                        TotalBytes = config.Length,
                        IsGated = false
                    };
                });

        public void FailHubConfig(Exception exception) =>
            Host.Checkpoints.FetchConfigAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException<BaseCheckpointManifest>(exception));

        public async Task<ModelLabPyTorchOutcome> ExpectAsync(ModelLabPyTorchRefusal expected, ModelLabPyTorchBuildRequest request)
        {
            var outcome = await Service.BuildAsync(request, CancellationToken.None);
            AssertEx.Null(outcome.Index, $"Expected {expected}.");
            AssertEx.Equal(expected, outcome.Refusal!.Value, outcome.RefusalMessage);
            AssertEx.NotEmpty(outcome.RefusalMessage);
            return outcome;
        }

        public async Task ExpectSourceAsync(ModelLabPyTorchRefusal expected, string indexId, string file)
        {
            var read = await Service.ReadSourceAsync(indexId, file, CancellationToken.None);
            AssertEx.Null(read.Source, $"{file} must not be served.");
            AssertEx.Equal(expected, read.Refusal!.Value, read.RefusalMessage);
        }

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            _unused.Dispose();
        }
    }

    /// <summary>A training venv on disk as uv lays it out: the interpreter, pyvenv.cfg, and transformers and torch with their dist-info.</summary>
    internal sealed class FakeRuntime : IDisposable
    {
        public const string ModelingSource = "class Qwen3_5DecoderLayer:\n    def forward(self, x):\n        return x\n";
        public const string LinearSource = "class Linear:\n    def forward(self, x):\n        return x\n";

        public FakeRuntime()
        {
            Root = Path.Combine(Path.GetTempPath(), "xe-pytorch-runtime-" + Guid.NewGuid().ToString("N"));
            Venv = Path.Combine(Root, "venv", ".venv");
            Interpreter = Path.Combine(Venv, "bin", "python");
            SitePackages = Path.Combine(Venv, "lib", "python3.12", "site-packages");
            _ = Directory.CreateDirectory(Path.GetDirectoryName(Interpreter)!);
            File.WriteAllText(Interpreter, string.Empty);
            File.WriteAllText(Path.Combine(Venv, "pyvenv.cfg"), "home = /usr/bin\n");
            _ = Directory.CreateDirectory(Path.Combine(SitePackages, "transformers-5.17.0.dist-info"));
            _ = Directory.CreateDirectory(Path.Combine(SitePackages, "torch-2.13.0+cpu.dist-info"));
            _ = Write(ModelingFile, ModelingSource);
            _ = Write(TorchFile, LinearSource);
        }

        public string Root { get; }

        public string Venv { get; }

        public string Interpreter { get; }

        public string SitePackages { get; }

        public const string TransformersVersion = "5.17.0";

        public string TorchVersion { get; private set; } = "2.13.0+cpu";

        /// <summary>Writes a file under the package its index name points at, and returns its hash.</summary>
        public string Write(string file, string content)
        {
            var path = file.StartsWith("torch:", StringComparison.Ordinal)
                ? Path.Combine(SitePackages, "torch", file["torch:".Length..])
                : Path.Combine(SitePackages, "transformers", file);
            _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return Sha256(content);
        }

        public void SetTorchVersion(string version)
        {
            Directory.Move(Path.Combine(SitePackages, $"torch-{TorchVersion}.dist-info"), Path.Combine(SitePackages, $"torch-{version}.dist-info"));
            TorchVersion = version;
        }

        public void Dispose() =>
            BestEffortFile.TryDeleteDirectory(Root);
    }

    /// <summary>A scripted <c>modellab.py</c> for source jobs: it records each job, writes the result it is given and says its lines.</summary>
    internal sealed class ScriptedSourceJob : ITrainingProcessSpawner
    {
        private readonly FakeRuntime _runtime;
        private int _spawns;

        public ScriptedSourceJob(FakeRuntime runtime)
        {
            _runtime = runtime;
            Result = () => ResultJson();
        }

        public Func<string?> Result { get; set; }

        public IReadOnlyList<string> Lines { get; init; } = ModelLabExperimentServiceTests.CompleteRun;

        public int ExitCode { get; init; }

        public IReadOnlyList<ModelLabPyTorchFile> ExtraFiles { get; init; } = [];

        /// <summary>Set to keep each job running until the test completes it.</summary>
        public TaskCompletionSource? Hold { get; init; }

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int SpawnCount => Volatile.Read(ref _spawns);

        public string? LastJob { get; private set; }

        public IReadOnlyList<string> LastConfigFiles { get; private set; } = [];

        public string? LastConfig { get; private set; }

        public string ResultJson(string? torchVersion = null) =>
            JsonSerializer.Serialize(new ModelLabPyTorchIndex
            {
                TransformersVersion = FakeRuntime.TransformersVersion,
                TorchVersion = torchVersion ?? _runtime.TorchVersion,
                ModelType = "qwen3_5",
                TextModelType = "qwen3_5_text",
                ModelClass = "Qwen3_5ForCausalLM",
                BlockPath = "model.layers",
                ExportMode = "representative",
                AnchoredLines = true,
                Outside =
                [
                    new ModelLabPyTorchModule
                    {
                        Path = "lm_head",
                        Class = "Linear",
                        File = TorchFile,
                        ClassStart = 1,
                        ClassEnd = 3,
                        ForwardLine = 2
                    }
                ],
                Files =
                [
                    new ModelLabPyTorchFile
                    {
                        File = ModelingFile,
                        Sha256 = Sha256(FakeRuntime.ModelingSource)
                    },
                    new ModelLabPyTorchFile
                    {
                        File = TorchFile,
                        Sha256 = Sha256(FakeRuntime.LinearSource)
                    },
                    .. ExtraFiles
                ]
            }, JsonSerializerOptions.Web);

        public ITrainingProcessHandle Spawn(TrainingSpawnRequest request)
        {
            _ = Interlocked.Increment(ref _spawns);
            LastJob = File.ReadAllText(request.Arguments[2]);
            using var job = JsonDocument.Parse(LastJob);
            var configDirectory = job.RootElement.GetProperty("configDirectory").GetString()!;
            LastConfigFiles = [.. Directory.GetFiles(configDirectory).Select(static path => Path.GetFileName(path))];
            LastConfig = File.ReadAllText(Path.Combine(configDirectory, "config.json"));
            var handle = new Handle(this, job.RootElement.GetProperty("resultPath").GetString()!);
            Started.TrySetResult();
            return handle;
        }

        private sealed class Handle : ITrainingProcessHandle
        {
            private readonly ScriptedSourceJob _owner;
            private readonly string _resultPath;

            public Handle(ScriptedSourceJob owner, string resultPath)
            {
                _owner = owner;
                _resultPath = resultPath;
            }

            public TrainingLaunchReceipt Receipt { get; } = new()
            {
                Pid = 4343,
                Pgid = 4343,
                ExecutablePath = "/venv/bin/python",
                StartTicks = 1,
                RunToken = "token"
            };

            public async IAsyncEnumerable<string> ReadOutputAsync([EnumeratorCancellation] CancellationToken cancellationToken)
            {
                if (_owner.Hold is { } hold)
                {
                    await hold.Task.WaitAsync(cancellationToken);
                }

                if (_owner.Result() is { } result)
                {
                    await File.WriteAllTextAsync(_resultPath, result, cancellationToken);
                }

                foreach (var line in _owner.Lines)
                {
                    yield return line;
                }
            }

            public Task<int> WaitForExitAsync(CancellationToken cancellationToken) =>
                Task.FromResult(_owner.ExitCode);

            public void RequestStop()
            {
            }

            public void KillGroup()
            {
            }

            public void Dispose()
            {
            }
        }
    }
}
