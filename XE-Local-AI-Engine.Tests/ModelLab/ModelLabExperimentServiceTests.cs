namespace XE_Local_AI_Engine.Tests.ModelLab;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Capacity.Implementation;
using XE_Local_AI_Engine.Client.Services.Common;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Client.Services.ModelLab;
using XE_Local_AI_Engine.Client.Services.Training.BaseArtifacts;
using XE_Local_AI_Engine.Client.Services.Training.Runs;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.HuggingFace.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.Training.Contracts;
using XE_Local_AI_Engine.Tests.Capacity;
using XE_Local_AI_Engine.Tests.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.Testing;
using static ModelLabTestSupport;

/// <summary>
///     The job on the node's real DI: real GPU gate, supervisor lease, footprint ledger, capacity gate, store and blob store. Only the
///     process, the runtime's install state, the card and the GGUF header are scripted.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class ModelLabExperimentServiceTests
{
    [Test]
    public async Task AStartedJob_HoldsTheRealAdmissionPath_AndGivesItAllBack()
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process);

        var start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);

        AssertEx.True(start.ExperimentId.HasValue, start.RefusalMessage ?? "The job should have started.");
        var id = start.ExperimentId!.Value;
        await process.Spawned.WaitAsync(TestBudgets.Contended);
        AssertEx.Equal(GpuWorkKind.ModelLabExperiment, host.Gate.ExclusiveKind!.Value, "The job holds the exclusive gate as its own kind.");
        AssertEx.True(host.Supervisor.IsKeepWarmSuppressed(), "The job holds the runtime-mutation lease a chat's load waits behind.");
        AssertEx.Equal(WeightBytes + ModelLabCheckpointPreflightService.WorkingMemoryBytes, host.Ledger.Reserved.GpuBytes, "The job's bytes are on the ledger every spawn reads.");

        var second = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);
        AssertEx.Equal(ModelLabStartRefusal.Busy, second.Refusal!.Value, "One job at a time, refused when busy.");

        process.Finish(CompleteRun, exitCode: 0);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);

        AssertEx.Null(host.Gate.ExclusiveKind, "The gate comes back.");
        AssertEx.False(host.Supervisor.IsKeepWarmSuppressed(), "The lease comes back.");
        AssertEx.Equal(expected: 0L, host.Ledger.Reserved.GpuBytes, "The bytes come back.");
        var record = AssertEx.NotNull(await host.Reader.GetAsync(id, CancellationToken.None));
        AssertEx.Equal(ModelLabExperimentStatus.Succeeded, record.Status);
        AssertEx.Equal(Prompt, record.Prompt);
        AssertEx.False(Directory.Exists(host.WorkDirectory(id)), "No plaintext work directory survives the job.");
    }

    /// <summary>The operations feed of every open tab learns of a start from this broadcast; a refused start announces nothing.</summary>
    [Test]
    public async Task AStart_IsAnnouncedOnce_AndARefusedOneNot()
    {
        using var process = new ScriptedProcess();
        var jobs = Substitute.For<IJobCreatedPublisher>();
        await using var host = await LabHost.StartAsync(process, configure: services =>
        {
            services.RemoveAll<IJobCreatedPublisher>();
            services.AddSingleton(jobs);
        });

        var start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);
        AssertEx.True(start.ExperimentId.HasValue, start.RefusalMessage ?? "The job should have started.");
        var id = start.ExperimentId!.Value;
        var refused = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);

        AssertEx.Equal(ModelLabStartRefusal.Busy, refused.Refusal!.Value);
        await jobs.Received(1).PublishCreatedAsync(CreatedJobKind.ModelLabExperiment, id, Arg.Any<CancellationToken>());
        AssertEx.Equal(expected: 1, jobs.ReceivedCalls().Count(), "Only the start that ran is announced.");
        await process.Spawned.WaitAsync(TestBudgets.Contended);
        process.Finish(CompleteRun, exitCode: 0);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);
    }

    /// <summary>
    ///     The self-preemption guard on the node's real DI: a holder registered before the admission (as the warm session
    ///     registers) sees no demand from the job's own gate and lease. The lease call at the end proves the wiring is live.
    /// </summary>
    [Test]
    public async Task AColdAdmission_ThroughTheRealGateChain_RaisesNoDemand()
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process);
        var broker = host.Services.GetRequiredService<GpuIdleHolderBroker>();
        var holder = new FakeGpuIdleHolder(static () => Task.CompletedTask);
        broker.Register(holder);
        try
        {
            var start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);

            AssertEx.True(start.ExperimentId.HasValue, start.RefusalMessage ?? "The job should have started.");
            await process.Spawned.WaitAsync(TestBudgets.Contended);
            AssertEx.Equal(expected: 0, holder.PreemptionRequestCount, "The job's own gate or lease raised demand.");
            process.Finish(CompleteRun, exitCode: 0);
            await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);

            await using var ordinary = await host.Supervisor.AcquireRuntimeMutationLeaseOrNullAsync(CancellationToken.None);
            AssertEx.NotNull(ordinary);
            AssertEx.Equal(expected: 1, holder.PreemptionRequestCount, "An ordinary lease on the same supervisor does raise demand.");
        }
        finally
        {
            broker.Deregister(holder);
        }
    }

    /// <summary>
    ///     A runtime install publishes its phase first and then sees a held lease as a running job. A start whose lease lands
    ///     after that publish must therefore read the phase again under the lease and refuse, giving everything back.
    /// </summary>
    [Test]
    public async Task AStart_WhoseLeaseLandsAfterARuntimeInstallPublishedItsPhase_IsRefused_AndGivesEverythingBack()
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process);
        host.Runtime.GetStatus().Returns(_ => RuntimeStatus(host.Supervisor.IsKeepWarmSuppressed() ? TrainingRuntimePhase.AcquiringUv : TrainingRuntimePhase.Ready));

        var start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);

        AssertEx.Equal(ModelLabStartRefusal.RuntimeUnavailable, start.Refusal!.Value);
        AssertEx.Equal(ModelLabRuntimeJob.RuntimeChangingMessage, start.RefusalMessage);
        AssertEx.Null(process.Request, "Nothing was spawned.");
        AssertEx.Null(host.Gate.ExclusiveKind, "The gate comes back.");
        AssertEx.False(host.Supervisor.IsKeepWarmSuppressed(), "The lease comes back, so the install's own check sees nothing running.");
        AssertEx.Equal(expected: 0L, host.Ledger.Reserved.GpuBytes);
        AssertEx.Empty(await host.Reader.ListAsync(CancellationToken.None));
    }

    /// <summary>A runtime being installed or removed may still exist; "not installed" would send the operator to install what is already coming.</summary>
    [Test]
    [Arguments(TrainingRuntimePhase.AcquiringUv)]
    [Arguments(TrainingRuntimePhase.ProvisioningPython)]
    [Arguments(TrainingRuntimePhase.InstallingPackages)]
    [Arguments(TrainingRuntimePhase.Verifying)]
    [Arguments(TrainingRuntimePhase.Removing)]
    public async Task AStart_WhileTheRuntimeIsChanging_IsRefusedAsChanging_NotAsMissing(TrainingRuntimePhase phase)
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process);
        host.Runtime.GetStatus().Returns(RuntimeStatus(phase));

        var start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);

        AssertEx.Equal(ModelLabStartRefusal.RuntimeUnavailable, start.Refusal!.Value);
        AssertEx.Equal(ModelLabRuntimeJob.RuntimeChangingMessage, start.RefusalMessage);
        AssertEx.Null(host.Gate.ExclusiveKind, "Refused before the gate.");
    }

    [Test]
    public async Task AStart_WithNoRuntimeInstalled_IsRefusedAsNotInstalled()
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process);
        host.Runtime.GetStatus().Returns(RuntimeStatus(TrainingRuntimePhase.Idle));
        host.Runtime.ResolveInterpreterPath().Returns((string?)null);

        var start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);

        AssertEx.Equal(ModelLabStartRefusal.RuntimeUnavailable, start.Refusal!.Value);
        AssertEx.Contains(start.RefusalMessage!, "is not installed", StringComparison.Ordinal);
    }

    [Test]
    public async Task TheJobFile_CarriesTheFixedSpec_AndTheQuarterPoints_WhenTheRequestNamesNoLayers()
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process);

        var start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);
        await process.Spawned.WaitAsync(TestBudgets.Contended);

        using var job = JsonDocument.Parse(process.ReadJobFile());
        var root = job.RootElement;
        AssertEx.Equal(ModelLabTraceSpec.ContractVersion, root.GetProperty("contractVersion").GetInt32());
        AssertEx.Equal(ModelLabTraceSpec.MaxNewTokens, root.GetProperty("maxNewTokens").GetInt32());
        AssertEx.Equal(ModelLabTraceSpec.TraceByteCapFor(3, ModelLabTraceSpec.MaxNewTokens), root.GetProperty("traceByteCap").GetInt32());
        AssertEx.Equal(ModelLabTraceSpec.RoutingByteCapFor(24, ModelLabTraceSpec.MaxNewTokens), root.GetProperty("routingByteCap").GetInt32(),
            "Every trace within the block cap carries one; the script records routing only for a model with routers.");
        AssertEx.Equal("[6,12,18]", root.GetProperty("layers").GetRawText());
        AssertEx.Equal(Prompt, root.GetProperty("prompt").GetString());
        foreach (var attentionField in new[]
                 {
                     "attentionPath",
                     "attentionLayers",
                     "attentionTopKeys",
                     "attentionByteCap"
                 })
        {
            AssertEx.False(root.TryGetProperty(attentionField, out _), $"A trace without attention leaves {attentionField} out, so the script sees all four absent.");
        }

        AssertEx.Equal("modellab.py", Path.GetFileName(process.Request!.Arguments[0]));
        AssertEx.Equal("--serve", process.Request.Arguments[1]);
        AssertEx.True(process.Request.RedirectStandardInput, "A serving worker reads its commands from stdin.");
        AssertEx.False(string.IsNullOrEmpty(root.GetProperty("modelKey").GetString()), "The job names the model the worker holds.");
        if (OperatingSystem.IsLinux())
        {
            AssertEx.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(process.JobFilePath!), "It holds the plaintext prompt.");
        }

        process.Finish(CompleteRun, exitCode: 0);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);
        AssertEx.True(start.ExperimentId.HasValue);
    }

    [Test]
    public async Task AFinishedTrace_IsEncryptedOnDisk_AndReadsBackAsItWasMeasured()
    {
        const string record = """{"s":0,"l":6,"k":"F","n":3.5,"p":[-1.0,0.0,1.0],"ti":[7,8],"tv":[0.5,-0.25]}""";
        const string footer = """{"footer":true,"truncated":true,"records":1,"dropped":9,"payloadBytes":10,"cap":262144}""";
        using var process = new ScriptedProcess
        {
            TraceContent = record + "\n" + footer + "\n"
        };
        await using var host = await LabHost.StartAsync(process);
        var start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);
        process.Finish(CompleteRun, exitCode: 0);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);
        var id = start.ExperimentId!.Value;

        var trace = await host.Reader.ReadTraceAsync(id, CancellationToken.None);

        AssertEx.Equal(ModelLabTraceReadStatus.Found, trace.Status);
        var read = trace.Records.Single();
        AssertEx.Equal(expected: 6, read.Layer);
        AssertEx.Equal(expected: 3.5, read.Norm);
        AssertEx.Equal("F", read.Kind);
        AssertEx.True(trace.Footer!.Truncated, "A truncated trace says so.");
        AssertEx.Equal(expected: 9, trace.Footer.Dropped);
        var blob = Directory.GetFiles(Path.Combine(host.DataRoot, "model-lab", "artifacts", id.ToString("N"))).Single();
        AssertEx.False(Encoding.UTF8.GetString(await File.ReadAllBytesAsync(blob)).Contains("\"footer\"", StringComparison.Ordinal), "The blob on disk is ciphertext.");
    }

    [Test]
    public async Task ATamperedTrace_IsUnavailable_NotServed()
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process);
        var start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);
        process.Finish(CompleteRun, exitCode: 0);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);
        var id = start.ExperimentId!.Value;
        var blob = Directory.GetFiles(Path.Combine(host.DataRoot, "model-lab", "artifacts", id.ToString("N"))).Single();
        var bytes = await File.ReadAllBytesAsync(blob);
        bytes[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(blob, bytes);

        var trace = await host.Reader.ReadTraceAsync(id, CancellationToken.None);

        AssertEx.Equal(ModelLabTraceReadStatus.Unavailable, trace.Status);
    }

    [Test]
    public async Task ATraceOverItsByteCap_IsNotKept_EvenWhenTheScriptClaimsSuccess()
    {
        using var process = new ScriptedProcess
        {
            TraceContent = new string('x', ModelLabTraceSpec.TraceByteCapFor(3, ModelLabTraceSpec.MaxNewTokens) + ModelLabTraceSpec.LensByteCapFor(24, ModelLabTraceSpec.MaxNewTokens)
                                                                                                                + ModelLabTraceSpec.RoutingByteCapFor(24, ModelLabTraceSpec.MaxNewTokens) + 1)
        };
        await using var host = await LabHost.StartAsync(process);
        var start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);
        process.Finish(CompleteRun, exitCode: 0);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);
        var id = start.ExperimentId!.Value;

        var record = AssertEx.NotNull(await host.Reader.GetAsync(id, CancellationToken.None));

        AssertEx.Equal(ModelLabExperimentStatus.Failed, record.Status);
        AssertEx.True(record.ErrorMessage!.Contains("byte cap", StringComparison.Ordinal), record.ErrorMessage);
        AssertEx.Null(record.TraceBlobId);
        AssertEx.False(Directory.Exists(Path.Combine(host.DataRoot, "model-lab", "artifacts", id.ToString("N"))), "Nothing over the cap reaches the store.");
    }

    /// <summary>Ascending because the script counts a token step at the first listed layer's hook: an earlier block listed later would be filed a step behind.</summary>
    [Test]
    public async Task TheRequestsLayers_ReachTheJobAscending_WithACapForTheirCount()
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process);

        var start = await host.Service.StartExperimentAsync(host.Request() with
        {
            Layers = [18, 0, 15, 23]
        }, CancellationToken.None);
        await process.Spawned.WaitAsync(TestBudgets.Contended);

        using (var job = JsonDocument.Parse(process.ReadJobFile()))
        {
            AssertEx.Equal("[0,15,18,23]", job.RootElement.GetProperty("layers").GetRawText());
            AssertEx.Equal(ModelLabTraceSpec.TraceByteCapFor(4, ModelLabTraceSpec.MaxNewTokens), job.RootElement.GetProperty("traceByteCap").GetInt32());
        }

        process.Finish(CompleteRun, exitCode: 0);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);
        AssertEx.True(start.ExperimentId.HasValue);
    }

    /// <summary>Every byte cap the job carries is sized for the tokens the request asked for, not for the default count.</summary>
    [Test]
    public async Task TheRequestsTokenCount_ReachesTheJob_WithEveryCapScaledForIt()
    {
        const int tokens = ModelLabTraceSpec.MaxRequestedNewTokens;
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process);

        var start = await host.Service.StartExperimentAsync(host.Request() with
        {
            MaxNewTokens = tokens,
            RecordAttention = true
        }, CancellationToken.None);
        await process.Spawned.WaitAsync(TestBudgets.Contended);

        using (var job = JsonDocument.Parse(process.ReadJobFile()))
        {
            var root = job.RootElement;
            AssertEx.Equal(tokens, root.GetProperty("maxNewTokens").GetInt32());
            AssertEx.Equal(ModelLabTraceSpec.TraceByteCapFor(3, tokens), root.GetProperty("traceByteCap").GetInt32());
            AssertEx.Equal(ModelLabTraceSpec.LensByteCapFor(24, tokens), root.GetProperty("lensByteCap").GetInt32());
            AssertEx.Equal(ModelLabTraceSpec.RoutingByteCapFor(24, tokens), root.GetProperty("routingByteCap").GetInt32());
            AssertEx.Equal(ModelLabTraceSpec.AttentionByteCapFor(6, 8, tokens), root.GetProperty("attentionByteCap").GetInt32());
        }

        process.Finish(CompleteRun, exitCode: 0);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);
        AssertEx.True(start.ExperimentId.HasValue);
    }

    /// <summary>All 16 OLMoE blocks are full attention, so the recording keeps 8 of them evenly, first and last included.</summary>
    [Test]
    [Arguments("")]
    [Arguments(
        ""","layer_types":["full_attention","full_attention","full_attention","full_attention","full_attention","full_attention","full_attention","full_attention","full_attention","full_attention","full_attention","full_attention","full_attention","full_attention","full_attention","full_attention"]""")]
    public async Task AnOlmoeTrace_RecordsAttention_AtEightEvenlySpacedBlocks(string extra)
    {
        const int tokens = ModelLabTraceSpec.MaxRequestedNewTokens;
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process, config: OlmoeConfig(extra), ggufBlockCount: 16, ggufArchitecture: "olmoe");

        var start = await host.Service.StartExperimentAsync(host.Request() with
        {
            MaxNewTokens = tokens,
            RecordAttention = true
        }, CancellationToken.None);
        AssertEx.True(start.ExperimentId.HasValue, start.RefusalMessage ?? "The trace starts.");
        await process.Spawned.WaitAsync(TestBudgets.Contended);

        using (var job = JsonDocument.Parse(process.ReadJobFile()))
        {
            AssertEx.Equal("[0,2,4,6,8,10,12,15]", job.RootElement.GetProperty("attentionLayers").GetRawText());
            AssertEx.Equal(expected: 20_971_680, job.RootElement.GetProperty("attentionByteCap").GetInt32());
            AssertEx.Equal(expected: 746_656, job.RootElement.GetProperty("routingByteCap").GetInt32(), "Routing sized for all 16 blocks at 256 tokens.");
        }

        process.Finish(CompleteRun, exitCode: 0);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);
    }

    [Test]
    [Arguments(
        ""","layer_types":["full_attention","linear_attention","full_attention","full_attention","full_attention","full_attention","full_attention","full_attention","full_attention","full_attention","full_attention","full_attention","full_attention","full_attention","full_attention","full_attention"]""")]
    [Arguments(""","sliding_window":4096""")]
    [Arguments(""","attention_chunk_size":8192""")]
    public async Task AnOlmoeConfigThatContradictsFullAttention_IsRefusedAttention_BeforeTheGate(string extra)
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process, config: OlmoeConfig(extra), ggufBlockCount: 16, ggufArchitecture: "olmoe");

        var start = await host.Service.StartExperimentAsync(host.Request() with
        {
            RecordAttention = true
        }, CancellationToken.None);

        AssertEx.Equal(ModelLabStartRefusal.AttentionUnavailable, start.Refusal!.Value);
        AssertEx.Contains(start.RefusalMessage!, "OLMoE checkpoint's config", StringComparison.Ordinal);
        AssertEx.Null(process.Request);
        AssertEx.Null(host.Gate.ExclusiveKind);
    }

    /// <summary>The script would refuse it only after loading the weights, so the node refuses the trace before the gate.</summary>
    [Test]
    public async Task AnOlmoeConfigThatAsksForRouterLogits_IsRefusedATrace_BeforeTheGate()
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process, config: OlmoeConfig(""","output_router_logits":true"""), ggufBlockCount: 16, ggufArchitecture: "olmoe");

        var start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);

        AssertEx.Equal(ModelLabStartRefusal.PreflightRefused, start.Refusal!.Value);
        AssertEx.Contains(start.RefusalMessage!, "output_router_logits", StringComparison.Ordinal);
        AssertEx.Null(process.Request);
        AssertEx.Null(host.Gate.ExclusiveKind);
    }

    [Test]
    public async Task TheStartRoute_RefusesATokenCountOutsideItsBounds_AndAnyForASteeringJob()
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process);
        using var client = host.Factory.CreateClient();

        foreach (var body in new object[]
                 {
                     TokenBody(host, 0),
                     TokenBody(host, ModelLabTraceSpec.MaxRequestedNewTokens + 1),
                     new
                     {
                         baseArtifactId = host.Request().BaseArtifactId,
                         installedModelName = Gguf,
                         prompt = Prompt,
                         licenseConfirmed = true,
                         maxNewTokens = 64,
                         steering = new
                         {
                             positivePrompts = new[]
                             {
                                 "a"
                             },
                             negativePrompts = new[]
                             {
                                 "b"
                             }
                         }
                     }
                 })
        {
            using var request = host.Authorized(HttpMethod.Post, "/api/local/v1/model-lab/experiments");
            request.Content = JsonContent.Create(body);
            using var response = await client.SendAsync(request);
            AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode, JsonSerializer.Serialize(body));
        }

        AssertEx.Null(process.Request);
    }

    /// <summary>Every block of the 24: the store must keep what the scaled cap, the lens cap and the routing cap allow, which is far past the three-layer cap, and nothing past it.</summary>
    [Test]
    [Arguments(0, ModelLabExperimentStatus.Succeeded)]
    [Arguments(1, ModelLabExperimentStatus.Failed)]
    public async Task AFullDepthTrace_IsKeptUpToItsScaledCap_AndNotPastIt(int bytesOverCap, ModelLabExperimentStatus expected)
    {
        var cap = ModelLabTraceSpec.TraceByteCapFor(24, ModelLabTraceSpec.MaxNewTokens);
        var fileCap = cap + ModelLabTraceSpec.LensByteCapFor(24, ModelLabTraceSpec.MaxNewTokens) + ModelLabTraceSpec.RoutingByteCapFor(24, ModelLabTraceSpec.MaxNewTokens);
        using var process = new ScriptedProcess
        {
            TraceContent = new string('x', fileCap + bytesOverCap)
        };
        await using var host = await LabHost.StartAsync(process);

        var start = await host.Service.StartExperimentAsync(host.Request() with
        {
            Layers = [.. Enumerable.Range(0, 24)]
        }, CancellationToken.None);
        await process.Spawned.WaitAsync(TestBudgets.Contended);
        using (var job = JsonDocument.Parse(process.ReadJobFile()))
        {
            AssertEx.Equal(cap, job.RootElement.GetProperty("traceByteCap").GetInt32());
        }

        process.Finish(CompleteRun, exitCode: 0);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);

        var record = AssertEx.NotNull(await host.Reader.GetAsync(start.ExperimentId!.Value, CancellationToken.None));
        AssertEx.Equal(expected, record.Status, record.ErrorMessage ?? "no error");
        AssertEx.Equal(bytesOverCap == 0 ? (long)fileCap : null, record.TraceByteCount);
    }

    [Test]
    [Arguments(24)]
    [Arguments(99)]
    public async Task ATraceLayerPastTheCheckpointsBlocks_IsRefusedBeforeTheGate(int pastTheEnd)
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process);

        var start = await host.Service.StartExperimentAsync(host.Request() with
        {
            Layers = [3, pastTheEnd]
        }, CancellationToken.None);

        AssertEx.Equal(ModelLabStartRefusal.InvalidTraceLayers, start.Refusal!.Value);
        AssertEx.Contains(start.RefusalMessage!, "blocks 0 to 23", StringComparison.Ordinal);
        AssertEx.Null(process.Request);
        AssertEx.Null(host.Gate.ExclusiveKind);
        AssertEx.Equal(expected: 0, (await host.Reader.ListAsync(CancellationToken.None)).Count);
    }

    [Test]
    public async Task TheStartRoute_RefusesTraceLayersTheValidatorBounds()
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process);
        using var client = host.Factory.CreateClient();

        foreach (var body in new object[]
                 {
                     TraceBody(host, Array.Empty<int>()),
                     TraceBody(host, [3, 3]),
                     TraceBody(host, [-1, 3]),
                     TraceBody(host, [.. Enumerable.Range(0, ModelLabTraceSpec.MaxTraceLayers + 1)]),
                     new
                     {
                         baseArtifactId = host.Request().BaseArtifactId,
                         installedModelName = Gguf,
                         prompt = Prompt,
                         licenseConfirmed = true,
                         layers = new[]
                         {
                             3
                         },
                         steering = new
                         {
                             positivePrompts = new[]
                             {
                                 "a"
                             },
                             negativePrompts = new[]
                             {
                                 "b"
                             }
                         }
                     }
                 })
        {
            using var request = host.Authorized(HttpMethod.Post, "/api/local/v1/model-lab/experiments");
            request.Content = JsonContent.Create(body);
            using var response = await client.SendAsync(request);
            AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode, JsonSerializer.Serialize(body));
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            AssertEx.False(string.IsNullOrWhiteSpace(problem.RootElement.GetProperty("errors")[0].GetProperty("reason").GetString()));
        }

        AssertEx.Null(process.Request);
    }

    [Test]
    public void TheTraceCap_ScalesWithTheLayerAndTokenCounts_AndNeverDropsBelowTheThreeLayerCap()
    {
        const int threeLayerCap = 256 * 1024;
        AssertEx.Equal(threeLayerCap, ModelLabTraceSpec.TraceByteCapFor(1, 128));
        AssertEx.Equal(threeLayerCap, ModelLabTraceSpec.TraceByteCapFor(3, 128), "Four times 3 x 128 x 162 bytes is a little under it.");
        AssertEx.Equal(threeLayerCap, ModelLabTraceSpec.TraceByteCapFor(64, 1));
        AssertEx.Equal((24 * 128 * 162 * 4) + 160, ModelLabTraceSpec.TraceByteCapFor(24, 128));
        AssertEx.Equal((32 * 128 * 162 * 4) + 160, ModelLabTraceSpec.TraceByteCapFor(32, 128));
        AssertEx.Equal((64 * 128 * 162 * 4) + 160, ModelLabTraceSpec.TraceByteCapFor(64, 128));
        AssertEx.Equal((64 * 256 * 162 * 4) + 160, ModelLabTraceSpec.TraceByteCapFor(64, 256));
        AssertEx.Throws<ArgumentOutOfRangeException>(static () => ModelLabTraceSpec.TraceByteCapFor(0, 128));
        AssertEx.Throws<ArgumentOutOfRangeException>(static () => ModelLabTraceSpec.TraceByteCapFor(ModelLabTraceSpec.MaxTraceLayers + 1, 128));
        AssertEx.Throws<ArgumentOutOfRangeException>(static () => ModelLabTraceSpec.TraceByteCapFor(3, 0));
        AssertEx.Throws<ArgumentOutOfRangeException>(static () => ModelLabTraceSpec.TraceByteCapFor(3, ModelLabTraceSpec.MaxRequestedNewTokens + 1));
    }

    [Test]
    public async Task Cancel_StopsTheScript_RecordsCancelled_AndKeepsNoTrace()
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process);
        var start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);
        await process.Spawned.WaitAsync(TestBudgets.Contended);
        var id = start.ExperimentId!.Value;

        AssertEx.True(host.Service.Cancel(id));
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);

        AssertEx.True(process.StopRequested, "The script gets the cooperative stop first.");
        AssertEx.False(process.Killed, "A script that obeys is not killed.");
        var record = AssertEx.NotNull(await host.Reader.GetAsync(id, CancellationToken.None));
        AssertEx.Equal(ModelLabExperimentStatus.Cancelled, record.Status);
        AssertEx.Null(record.TraceBlobId);
        AssertEx.Null(host.Gate.ExclusiveKind);
        AssertEx.False(Directory.Exists(host.WorkDirectory(id)));
        AssertEx.False(host.Service.Cancel(id), "A finished job has nothing to cancel.");
    }

    [Test]
    public async Task Cancel_KillsAScriptThatIgnoresTheStop_AndTheOutcomeIsStillCancelled()
    {
        using var process = new ScriptedProcess
        {
            IgnoresStop = true
        };
        await using var host = await LabHost.StartAsync(process, new ModelLabJobOptions
        {
            CancelAcknowledgementTimeout = TimeSpan.FromMilliseconds(50)
        });
        var start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);
        await process.Spawned.WaitAsync(TestBudgets.Contended);
        var id = start.ExperimentId!.Value;

        AssertEx.True(host.Service.Cancel(id));
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);

        AssertEx.True(process.Killed, "A wedged script cannot honour a cooperative stop.");
        AssertEx.Equal(ModelLabExperimentStatus.Cancelled, (await host.Reader.GetAsync(id, CancellationToken.None))!.Status);
    }

    [Test]
    [Arguments(139, "exited with status 139")]
    [Arguments(0, "exited with status 0")]
    public async Task AScriptThatDiesWithoutDone_FailsTheRow(int exitCode, string expected)
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process);
        var start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);
        process.Finish([Handshake], exitCode);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);

        var record = AssertEx.NotNull(await host.Reader.GetAsync(start.ExperimentId!.Value, CancellationToken.None));

        AssertEx.Equal(ModelLabExperimentStatus.Failed, record.Status);
        AssertEx.True(record.ErrorMessage!.Contains(expected, StringComparison.Ordinal), record.ErrorMessage);
        AssertEx.Null(host.Gate.ExclusiveKind, "A crash gives the GPU back.");
    }

    [Test]
    public async Task AScriptError_IsTheRowsReason()
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process);
        var start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);
        process.Finish([Handshake, """{"event":"error","category":"out-of-memory","message":"The GPU ran out of memory during the trace."}"""], exitCode: 1);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);

        var record = AssertEx.NotNull(await host.Reader.GetAsync(start.ExperimentId!.Value, CancellationToken.None));

        AssertEx.Equal("The GPU ran out of memory during the trace.", record.ErrorMessage!);
    }

    [Test]
    public async Task AFailedRow_KeepsTheLastLinesTheScriptPrinted_BecauseANativeToolExplainsItselfOnlyThere()
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process);
        var start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);
        var noise = Enumerable.Range(0, 30).Select(static i => $"noise {i}");
        process.Finish([Handshake, .. noise, "cuda_utils.c:9:10: fatal error: Python.h: No such file or directory", """{"event":"error","category":"trace","message":"CalledProcessError"}"""],
            exitCode: 1);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);

        var record = AssertEx.NotNull(await host.Reader.GetAsync(start.ExperimentId!.Value, CancellationToken.None));

        AssertEx.True(record.ErrorMessage!.StartsWith("CalledProcessError", StringComparison.Ordinal), record.ErrorMessage);
        AssertEx.True(record.ErrorMessage.Contains("Python.h", StringComparison.Ordinal), record.ErrorMessage);
        AssertEx.False(record.ErrorMessage.Contains("noise 0\n", StringComparison.Ordinal), "The tail is bounded.");
    }

    [Test]
    public async Task AnotherProtocolVersion_IsKilledAtTheHandshake()
    {
        using var process = new ScriptedProcess
        {
            IgnoresStop = true,
            ServeHandshake = $"{{\"event\":\"handshake\",\"contractVersion\":{ModelLabTraceSpec.ContractVersion + 1},\"serve\":true}}"
        };
        await using var host = await LabHost.StartAsync(process);
        var start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);

        AssertEx.True(process.Killed);
        var record = AssertEx.NotNull(await host.Reader.GetAsync(start.ExperimentId!.Value, CancellationToken.None));
        AssertEx.True(record.ErrorMessage!.Contains("protocol version", StringComparison.Ordinal), record.ErrorMessage);
    }

    [Test]
    public async Task ASilentScript_IsKilledByTheWatchdog()
    {
        using var process = new ScriptedProcess
        {
            IgnoresStop = true
        };
        await using var host = await LabHost.StartAsync(process, new ModelLabJobOptions
        {
            InactivityTimeout = TimeSpan.FromMilliseconds(50)
        });
        var start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);

        AssertEx.True(process.Killed);
        var record = AssertEx.NotNull(await host.Reader.GetAsync(start.ExperimentId!.Value, CancellationToken.None));
        AssertEx.Equal(ModelLabExperimentStatus.Failed, record.Status);
        AssertEx.True(record.ErrorMessage!.Contains("stopped reporting", StringComparison.Ordinal), record.ErrorMessage);
    }

    [Test]
    public async Task AScriptPastItsMaximumDuration_IsKilled_AndSaysSo()
    {
        using var process = new ScriptedProcess
        {
            IgnoresStop = true
        };
        // The short grace is what shortens the poll, since a bound is never rounded up to it.
        await using var host = await LabHost.StartAsync(process, new ModelLabJobOptions
        {
            MaxDuration = TimeSpan.FromMilliseconds(50),
            CancelAcknowledgementTimeout = TimeSpan.FromMilliseconds(200)
        });
        var start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);

        AssertEx.True(process.Killed);
        var record = AssertEx.NotNull(await host.Reader.GetAsync(start.ExperimentId!.Value, CancellationToken.None));
        AssertEx.Equal(ModelLabExperimentStatus.Failed, record.Status);
        AssertEx.True(record.ErrorMessage!.Contains("maximum duration", StringComparison.Ordinal), record.ErrorMessage);
    }

    [Test]
    public async Task AScriptThatClosesItsOutputAndNeverExits_IsKilled_AndSaysSo()
    {
        using var process = new ScriptedProcess
        {
            IgnoresStop = true
        };
        await using var host = await LabHost.StartAsync(process, new ModelLabJobOptions
        {
            CancelAcknowledgementTimeout = TimeSpan.FromMilliseconds(50)
        });
        var start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);
        await process.Spawned.WaitAsync(TestBudgets.Contended);
        process.CloseOutput();
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);

        AssertEx.True(process.Killed);
        var record = AssertEx.NotNull(await host.Reader.GetAsync(start.ExperimentId!.Value, CancellationToken.None));
        AssertEx.Equal(ModelLabExperimentStatus.Failed, record.Status);
        AssertEx.True(record.ErrorMessage!.Contains("stopped responding", StringComparison.Ordinal), record.ErrorMessage);
    }

    [Test]
    public async Task ProgressAndTheEnd_ReachTheRunBuffer_UnderTheExperimentsId()
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process);
        var seen = new List<TrainingRunEvent>();
        var buffer = host.Services.GetRequiredService<ITrainingRunEventBuffer>();
        buffer.EventPublished += (_, args) =>
        {
            lock (seen)
            {
                seen.Add(args.Event);
            }
        };

        var start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);
        process.Finish(CompleteRun, exitCode: 0);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);

        var id = start.ExperimentId!.Value;
        List<TrainingRunEvent> events;
        lock (seen)
        {
            events = seen.Where(item => item.RunId == id).ToList();
        }

        AssertEx.Equal(expected: 3, events.Count, "Phase, progress and the terminal state.");
        AssertEx.Equal(expected: 1, events[1].Payload.Step!.Value);
        AssertEx.Equal(nameof(ModelLabExperimentStatus.Succeeded), events[2].Payload.State!);
        AssertEx.True(buffer.Replay(id, afterSequence: 0).ResetRequired, "Once the job ends, a late subscriber is sent to the saved row.");
    }

    [Test]
    public async Task ALoadedModel_BlocksTheStart_WithoutEjectingIt()
    {
        // The real supervisor over its fake process seam, so the real "a process owns the runtime" predicate is what refuses.
        var launcher = new FakeProcessLauncher();
        await using var supervisor = SupervisorFactory.Create(launcher);
        _ = await supervisor.EnsureRunningAsync("loaded-model", ModelRole.Chat, CancellationToken.None);
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process, supervisor: supervisor);

        var start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);

        AssertEx.Equal(ModelLabStartRefusal.ModelLoaded, start.Refusal!.Value);
        AssertEx.True(start.RefusalMessage!.Contains("Eject", StringComparison.Ordinal), start.RefusalMessage);
        AssertEx.Null(process.Request, "Nothing was spawned.");
        AssertEx.Null(host.Gate.ExclusiveKind, "The refusal gives the gate back.");
        AssertEx.Equal(expected: 0, (await host.Reader.ListAsync(CancellationToken.None)).Count, "A refusal writes nothing.");
        AssertEx.False(supervisor.GetRegisteredProcess("loaded-model", ModelRole.Chat)!.Handle.HasExited, "The loaded model was not ejected.");
    }

    [Test]
    public async Task NotEnoughFreeMemory_IsRefusedByTheCapacityGate()
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process, availableVramBytes: 1024);

        var start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);

        AssertEx.Equal(ModelLabStartRefusal.InsufficientMemory, start.Refusal!.Value);
        AssertEx.Null(process.Request);
        AssertEx.False(host.Supervisor.IsKeepWarmSuppressed(), "The refusal gives the lease back.");
        AssertEx.Null(host.Gate.ExclusiveKind);
    }

    [Test]
    public async Task OtherGpuWork_IsBusy()
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process);
        AssertEx.True(host.Gate.TryBeginExclusive(GpuWorkKind.TrainingRun, out var hold));
        using (hold)
        {
            var start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);

            AssertEx.Equal(ModelLabStartRefusal.Busy, start.Refusal!.Value);
            AssertEx.Null(process.Request);
        }
    }

    [Test]
    public async Task EveryRequestLevelRefusal_HappensBeforeTheGateIsTaken()
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process);

        var unknown = await host.Service.StartExperimentAsync(host.Request() with
        {
            BaseArtifactId = Guid.NewGuid()
        }, CancellationToken.None);
        AssertEx.Equal(ModelLabStartRefusal.CheckpointNotFound, unknown.Refusal!.Value);

        var unconfirmed = await host.Service.StartExperimentAsync(host.Request() with
        {
            LicenseConfirmed = false
        }, CancellationToken.None);
        AssertEx.Equal(ModelLabStartRefusal.LicenseNotConfirmed, unconfirmed.Refusal!.Value);

        var uninstalled = await host.Service.StartExperimentAsync(host.Request() with
        {
            InstalledModelName = "org/Other-GGUF:Q4_K_M"
        }, CancellationToken.None);
        AssertEx.Equal(ModelLabStartRefusal.PreflightRefused, uninstalled.Refusal!.Value);

        var notReady = await host.Service.StartExperimentAsync(host.Request() with
        {
            BaseArtifactId = await host.SeedDownloadingArtifactAsync()
        }, CancellationToken.None);
        AssertEx.Equal(ModelLabStartRefusal.CheckpointNotReady, notReady.Refusal!.Value);

        host.Runtime.ResolveInterpreterPath().Returns((string?)null);
        var noRuntime = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);
        AssertEx.Equal(ModelLabStartRefusal.RuntimeUnavailable, noRuntime.Refusal!.Value);

        AssertEx.Null(process.Request);
        AssertEx.Equal(expected: 0, (await host.Reader.ListAsync(CancellationToken.None)).Count);
    }

    [Test]
    public async Task OtherPlatforms_AreToldWhy_BeforeAnythingHappens()
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process);
        host.Service.PlatformProbe = static () => false;

        var start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);
        var download = await host.Service.StartCheckpointDownloadAsync("Qwen/Qwen3.5-0.8B", revision: null, Gguf, licenseConfirmed: true, CancellationToken.None);

        AssertEx.Equal(ModelLabStartRefusal.UnsupportedPlatform, start.Refusal!.Value);
        AssertEx.Equal(ModelLabCheckpointRefusal.UnsupportedPlatform, download.Preflight.Refusal!.Value);
        AssertEx.True(start.RefusalMessage!.Contains("Linux", StringComparison.Ordinal), start.RefusalMessage);
        await host.Checkpoints.DidNotReceiveWithAnyArgs().ResolveAsync(default!, default, default);
    }

    [Test]
    public async Task ACheckpointAboveTheFitLimit_IsRefusedBeforeAnyTransfer()
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process);
        host.Checkpoints.ResolveAsync("Qwen/Qwen3.8-27B", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Manifest("Qwen/Qwen3.8-27B", 55_563_006_776));

        var download = await host.Service.StartCheckpointDownloadAsync("Qwen/Qwen3.8-27B", revision: null, Gguf, licenseConfirmed: true, CancellationToken.None);

        AssertEx.Equal(ModelLabCheckpointRefusal.CannotFitCard, download.Preflight.Refusal!.Value);
        AssertEx.True(download.Preflight.RefusalMessage!.Contains("cannot fit this card", StringComparison.Ordinal), download.Preflight.RefusalMessage);
        AssertEx.Null(download.BaseArtifactId);
        await host.Checkpoints.DidNotReceiveWithAnyArgs().DownloadAsync(default!, default!, default, default);
    }

    [Test]
    public async Task ADownload_NeedsTheLicenseConfirmed_AndFetchesOnlyTheConfigUntilThen()
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process);
        host.Checkpoints.ResolveAsync("Qwen/Qwen3.5-0.8B", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Manifest("Qwen/Qwen3.5-0.8B", WeightBytes));
        host.Checkpoints.DownloadAsync(Arg.Any<BaseCheckpointManifest>(), Arg.Any<string>(), Arg.Any<IProgress<PullProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var manifest = call.Arg<BaseCheckpointManifest>();
                var path = Path.Combine(call.Arg<string>(), "config.json");
                File.WriteAllText(path, LabHost.Config);
                return manifest with
                {
                    Files =
                    [
                        .. manifest.Files.Select(file => file with
                        {
                            LocalPath = path
                        })
                    ]
                };
            });

        var refused = await host.Service.StartCheckpointDownloadAsync("Qwen/Qwen3.5-0.8B", revision: null, Gguf, licenseConfirmed: false, CancellationToken.None);

        AssertEx.Equal(ModelLabCheckpointRefusal.LicenseNotConfirmed, refused.Preflight.Refusal!.Value);
        AssertEx.Null(refused.BaseArtifactId);
        AssertEx.True(refused.Preflight.RefusalMessage!.Contains("declares the license 'apache-2.0'", StringComparison.Ordinal), refused.Preflight.RefusalMessage);
        AssertEx.True(refused.Preflight.MetadataPresent, "The page translates this refusal from the preflight's facts, so they ride along with it.");
        await host.Checkpoints.DidNotReceive().DownloadAsync(Arg.Is<BaseCheckpointManifest>(manifest => manifest.Files.Any(file => file.Role == BaseCheckpointFileRole.Weights)),
            Arg.Any<string>(),
            Arg.Any<IProgress<PullProgress>?>(),
            Arg.Any<CancellationToken>());

        var allowed = await host.Service.StartCheckpointDownloadAsync("Qwen/Qwen3.5-0.8B", revision: null, Gguf, licenseConfirmed: true, CancellationToken.None);

        AssertEx.Null(allowed.Preflight.Refusal);
        AssertEx.True(allowed.BaseArtifactId.HasValue);
    }

    [Test]
    public async Task Retention_KeepsTheNewestTwenty_AndTakesTheOldestBlobsWithIt()
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process);
        var oldest = Guid.NewGuid();
        await host.SeedFinishedAsync(oldest);
        var oldestScope = Path.Combine(host.DataRoot, "model-lab", "artifacts", oldest.ToString("N"));
        Directory.CreateDirectory(oldestScope);
        await File.WriteAllTextAsync(Path.Combine(oldestScope, "x.blob"), "ciphertext");
        for (var i = 0; i < ModelLabExperimentService.MaxRetainedExperiments - 1; i++)
        {
            await host.SeedFinishedAsync(Guid.NewGuid());
        }

        var start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);
        process.Finish(CompleteRun, exitCode: 0);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);

        var rows = await host.Reader.ListAsync(CancellationToken.None);
        AssertEx.Equal(ModelLabExperimentService.MaxRetainedExperiments, rows.Count);
        AssertEx.False(rows.Any(row => row.Id == oldest), "The oldest row went.");
        AssertEx.True(rows.Any(row => row.Id == start.ExperimentId), "The new one stayed.");
        AssertEx.False(Directory.Exists(oldestScope), "Its blobs went with it.");
    }

    [Test]
    public async Task Retention_NeverTrimsAPinnedRun_ButAManualDeleteStillRemovesIt()
    {
        using var process = new ScriptedProcess();
        // Rows are ordered by a millisecond timestamp: on the system clock two quick seeds can tie, and the tie-break by
        // id then decides at random which of them is the oldest.
        await using var host = await LabHost.StartAsync(process, configure: static services => services.AddSingleton<TimeProvider>(new StrictlyIncreasingClock()));
        var pinned = Guid.NewGuid();
        await host.SeedFinishedAsync(pinned);
        AssertEx.Equal(ModelLabExperimentUpdateOutcome.Updated, await host.Reader.SetPinnedAsync(pinned, pinned: true, CancellationToken.None));
        var pinnedScope = SeedBlobScope(host, pinned);
        var oldestUnpinned = Guid.NewGuid();
        await host.SeedFinishedAsync(oldestUnpinned);
        var oldestUnpinnedScope = SeedBlobScope(host, oldestUnpinned);
        for (var i = 0; i < ModelLabExperimentService.MaxRetainedExperiments - 1; i++)
        {
            await host.SeedFinishedAsync(Guid.NewGuid());
        }

        var start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);
        process.Finish(CompleteRun, exitCode: 0);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);

        var rows = await host.Reader.ListAsync(CancellationToken.None);
        AssertEx.Equal(ModelLabExperimentService.MaxRetainedExperiments + 1, rows.Count, "Twenty unpinned runs plus the pinned one.");
        AssertEx.True(rows.Single(row => row.Id == pinned).IsPinned, "The oldest run is pinned, so it stays.");
        AssertEx.True(Directory.Exists(pinnedScope), "And so do its blobs.");
        AssertEx.False(rows.Any(row => row.Id == oldestUnpinned), "The oldest unpinned run went.");
        AssertEx.False(Directory.Exists(oldestUnpinnedScope), "Its blobs went with it.");
        AssertEx.Equal(AssertEx.NotNull(ModelLabExperimentService.AppVersion), rows.Single(row => row.Id == start.ExperimentId).AppVersion, "A new run records the node's version.");

        AssertEx.Equal(ModelLabExperimentDeleteOutcome.Deleted, await host.Service.DeleteAsync(pinned, CancellationToken.None));
        AssertEx.False(Directory.Exists(pinnedScope), "A manual delete takes a pinned run's blobs too.");
    }

    /// <summary>Wall time that moves forward by a millisecond on every read, so rows created in a row never share a timestamp; timers stay real.</summary>
    private sealed class StrictlyIncreasingClock : TimeProvider
    {
        private readonly DateTimeOffset _start = System.GetUtcNow();
        private long _reads;

        public override DateTimeOffset GetUtcNow() =>
            _start.AddMilliseconds(Interlocked.Increment(ref _reads));
    }

    [Test]
    public async Task ManualDelete_RemovesTheRowAndItsBlobs_AndRefusesARunningJob()
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process);
        var start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);
        await process.Spawned.WaitAsync(TestBudgets.Contended);
        var id = start.ExperimentId!.Value;

        AssertEx.Equal(ModelLabExperimentDeleteOutcome.Running, await host.Service.DeleteAsync(id, CancellationToken.None));

        process.Finish(CompleteRun, exitCode: 0);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);
        AssertEx.True(Directory.Exists(Path.Combine(host.DataRoot, "model-lab", "artifacts", id.ToString("N"))));

        AssertEx.Equal(ModelLabExperimentDeleteOutcome.Deleted, await host.Service.DeleteAsync(id, CancellationToken.None));

        AssertEx.False(Directory.Exists(Path.Combine(host.DataRoot, "model-lab", "artifacts", id.ToString("N"))));
        AssertEx.Equal(ModelLabExperimentDeleteOutcome.NotFound, await host.Service.DeleteAsync(id, CancellationToken.None));
    }

    [Test]
    public async Task ACheckpointALiveJobUses_CannotBeDeleted_UntilTheJobEnds()
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process);
        _ = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);
        await process.Spawned.WaitAsync(TestBudgets.Contended);
        var artifactRoute = $"/api/local/v1/training/base-artifacts/{host.Request().BaseArtifactId}";
        using var client = host.Factory.CreateClient();

        using (var request = host.Authorized(HttpMethod.Delete, artifactRoute))
            using (var response = await client.SendAsync(request))
            {
                AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                AssertEx.Equal("in-use-by-experiment", body.RootElement.GetProperty("reason").GetString());
                AssertEx.True(body.RootElement.GetProperty("message").GetString()!.Contains("Model Lab", StringComparison.Ordinal));
            }

        process.Finish(CompleteRun, exitCode: 0);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);

        using (var request = host.Authorized(HttpMethod.Delete, artifactRoute))
            using (var response = await client.SendAsync(request))
            {
                AssertEx.Equal(HttpStatusCode.NoContent, response.StatusCode);
            }
    }

    /// <summary>A failed <c>CompleteAsync</c> leaves such a row until the next startup. Nothing runs, so nothing is protected by refusing.</summary>
    [Test]
    public async Task ARunningRowWithNoLiveJob_BlocksNeitherItsCheckpoint_NorItsOwnDelete()
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process);
        var stranded = Guid.NewGuid();
        await host.SeedRunningAsync(stranded);
        using var client = host.Factory.CreateClient();

        AssertEx.Equal(ModelLabExperimentDeleteOutcome.Deleted, await host.Service.DeleteAsync(stranded, CancellationToken.None));

        await host.SeedRunningAsync(Guid.NewGuid());
        using var request = host.Authorized(HttpMethod.Delete, $"/api/local/v1/training/base-artifacts/{host.Request().BaseArtifactId}");
        using var response = await client.SendAsync(request);
        AssertEx.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    /// <summary>The receipt is the next startup's only way to find and kill an orphan.</summary>
    [Test]
    public async Task ARowStillHoldingAReceipt_IsRefusedDeletion_AndTheMessageSaysWhy()
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process);
        var id = Guid.NewGuid();
        await host.SeedFinishedAsync(id);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IModelLabExperimentStore>().SetLaunchReceiptAsync(id, new byte[]
            {
                1
            });
        }

        using var client = host.Factory.CreateClient();
        using var request = host.Authorized(HttpMethod.Delete, $"/api/local/v1/model-lab/experiments/{id}");
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        AssertEx.Equal(nameof(ModelLabStartRefusal.ProcessNotConfirmedStopped), body.RootElement.GetProperty("reason").GetString());
        AssertEx.True(body.RootElement.GetProperty("message").GetString()!.Contains("next start", StringComparison.Ordinal), "It says why, and when it clears.");
    }

    /// <summary>The page stops following a job when a list read says it ended; a read after the last event must say so, and a new start must be admitted.</summary>
    [Test]
    [Arguments(0, nameof(ModelLabExperimentStatus.Succeeded))]
    [Arguments(139, nameof(ModelLabExperimentStatus.Failed))]
    public async Task TheFinalStateEvent_IsVisibleOnlyOnceTheJobHasLetGoOfEverything(int exitCode, string expectedState)
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process);
        var buffer = host.Services.GetRequiredService<ITrainingRunEventBuffer>();
        string? state = null;
        Guid? activeAtEvent = Guid.NewGuid();
        var residentAtEvent = new ModelLabWarmResident
        {
            State = RuntimeResidentState.Active,
            ModelId = "unset"
        };
        var workDirectoryAtEvent = true;
        var start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);
        var id = start.ExperimentId!.Value;
        buffer.EventPublished += (_, args) =>
        {
            if (args.Event.RunId == id && args.Event.Payload.State is { } published)
            {
                state = published;
                activeAtEvent = host.Service.ActiveExperimentId;
                residentAtEvent = host.Service.GetResident();
                workDirectoryAtEvent = Directory.Exists(host.WorkDirectory(id));
            }
        };

        process.Finish(exitCode == 0 ? CompleteRun : [Handshake], exitCode);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);

        AssertEx.Equal(expectedState, state!);
        AssertEx.Null(activeAtEvent, "A list read after the event must not name the finished job.");
        // A start right after the event is admitted: it awaits the eviction under way, which never waits on a job.
        AssertEx.Null(residentAtEvent, "The session has left Active, here for the eviction warm time 0 asks for.");
        AssertEx.Null(host.Gate.ExclusiveKind, "The job's end waits for the eviction to give the gate back.");
        AssertEx.False(workDirectoryAtEvent, "The plaintext goes before the job announces its end.");
        AssertEx.True(buffer.Replay(id, afterSequence: 0).ResetRequired, "The buffer is still evicted, after the last event.");
    }

    [Test]
    public async Task TheFinalStateEvent_FollowsACancel_AfterTheJobHasLetGo()
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process);
        var buffer = host.Services.GetRequiredService<ITrainingRunEventBuffer>();
        Guid? activeAtEvent = Guid.NewGuid();
        var start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);
        var id = start.ExperimentId!.Value;
        await process.Spawned.WaitAsync(TestBudgets.Contended);
        buffer.EventPublished += (_, args) =>
        {
            if (args.Event.RunId == id && args.Event.Payload.State is not null)
            {
                activeAtEvent = host.Service.ActiveExperimentId;
            }
        };

        AssertEx.True(host.Service.Cancel(id));
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);

        AssertEx.Null(activeAtEvent);
    }

    /// <summary>The hub refuses an id with no row, and the page does not retry, so the id stays hidden until the commit. The delete guards hold throughout.</summary>
    [Test]
    public async Task TheJobsId_IsHiddenUntilItsRowCommits_WhileItsGuardsHoldFromAdmission()
    {
        LabHost? labHost = null;
        Guid? idBeforeCommit = Guid.NewGuid();
        Guid? artifactBeforeCommit = null;
        GpuWorkKind? gateBeforeCommit = null;
        var deleteAfterCommit = ModelLabExperimentDeleteOutcome.NotFound;
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process, configure: services => WrapStore(services, store => new FaultingStore(store)
        {
            BeforeCreate = () =>
            {
                idBeforeCommit = labHost!.Service.ActiveExperimentId;
                artifactBeforeCommit = labHost.Service.ActiveBaseArtifactId;
                gateBeforeCommit = labHost.Gate.ExclusiveKind;
            },
            BeforeTrim = async id => deleteAfterCommit = await labHost!.Service.DeleteAsync(id, CancellationToken.None)
        }));
        labHost = host;

        var start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);

        var idAfterStart = host.Service.ActiveExperimentId;
        process.Finish(CompleteRun, exitCode: 0);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);

        AssertEx.Null(idBeforeCommit);
        AssertEx.Equal(host.Request().BaseArtifactId, artifactBeforeCommit!.Value);
        AssertEx.Equal(GpuWorkKind.ModelLabExperiment, gateBeforeCommit!.Value);
        AssertEx.Equal(ModelLabExperimentDeleteOutcome.Running, deleteAfterCommit, "The row of a real job cannot be deleted in the gap after its commit.");
        AssertEx.Equal(start.ExperimentId!.Value, idAfterStart!.Value, "The start's answer can be followed at once.");
    }

    [Test]
    public async Task HostShutdown_CancelsTheJob_AndWaitsForIt()
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process);
        var start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);
        await process.Spawned.WaitAsync(TestBudgets.Contended);

        await host.Service.StopAsync(CancellationToken.None).WaitAsync(TestBudgets.Contended);

        AssertEx.True(process.StopRequested);
        AssertEx.Equal(ModelLabExperimentStatus.Cancelled, (await host.Reader.GetAsync(start.ExperimentId!.Value, CancellationToken.None))!.Status);
        AssertEx.Null(host.Gate.ExclusiveKind);
    }

    [Test]
    public async Task ARequestAbortedRightAfterTheRowCommits_StillRunsTheJob()
    {
        using var aborted = new CancellationTokenSource();
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process, configure: services => WrapStore(services, store => new FaultingStore(store)
        {
            AfterCreate = aborted.Cancel
        }));

        var start = await host.Service.StartExperimentAsync(host.Request(), aborted.Token);

        AssertEx.True(start.ExperimentId.HasValue, start.RefusalMessage ?? "The job should have started.");
        process.Finish(CompleteRun, exitCode: 0);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);
        AssertEx.Equal(ModelLabExperimentStatus.Succeeded, (await host.Reader.GetAsync(start.ExperimentId!.Value, CancellationToken.None))!.Status);
    }

    [Test]
    public async Task AFailedTrim_NeitherFailsTheStart_NorStrandsTheRow()
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process, configure: services => WrapStore(services, static store => new FaultingStore(store)
        {
            TrimFailure = new InvalidOperationException("database is locked")
        }));

        var start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);

        AssertEx.True(start.ExperimentId.HasValue, start.RefusalMessage ?? "The job should have started.");
        process.Finish(CompleteRun, exitCode: 0);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);
        AssertEx.Equal(ModelLabExperimentStatus.Succeeded, (await host.Reader.GetAsync(start.ExperimentId!.Value, CancellationToken.None))!.Status);
        AssertEx.Null(host.Gate.ExclusiveKind);
    }

    [Test]
    public async Task ATraceWithNonFiniteValues_ReadsBackThroughTheRoute_WithNulls()
    {
        const string record = """{"s":0,"l":6,"k":"F","n":null,"p":[null,0.0,1.0],"ti":[7,8],"tv":[null,-0.25]}""";
        const string footer = """{"footer":true,"truncated":false,"records":1,"dropped":0,"payloadBytes":10,"cap":262144}""";
        using var process = new ScriptedProcess
        {
            TraceContent = record + "\n" + footer + "\n"
        };
        await using var host = await LabHost.StartAsync(process);
        var start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);
        process.Finish(CompleteRun, exitCode: 0);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);

        using var client = host.Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/local/v1/model-lab/experiments/{start.ExperimentId}/trace");
        host.Factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var read = body.RootElement.GetProperty("records")[0];
        AssertEx.Equal(JsonValueKind.Null, read.GetProperty("norm").ValueKind);
        AssertEx.Equal(JsonValueKind.Null, read.GetProperty("percentiles")[0].ValueKind);
        AssertEx.Equal(expected: 1.0, read.GetProperty("percentiles")[2].GetDouble());
        AssertEx.Equal(JsonValueKind.Null, read.GetProperty("topValues")[0].ValueKind);
    }

    [Test]
    public async Task ACheckpointOnDisk_IsCheckedWithoutContactingTheHub()
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process);

        using var client = host.Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/local/v1/model-lab/checkpoint-preflight")
        {
            Content = JsonContent.Create(new
            {
                baseArtifactId = host.Request().BaseArtifactId,
                installedModelName = Gguf
            })
        };
        host.Factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        AssertEx.Equal(JsonValueKind.Null, body.RootElement.GetProperty("refusal").ValueKind);
        AssertEx.True(body.RootElement.GetProperty("licenseConfirmationText").GetString()!.Contains("apache-2.0", StringComparison.Ordinal));
        await host.Checkpoints.DidNotReceiveWithAnyArgs().ResolveAsync(default!, default, default);
    }

    /// <summary>"No result" must stay NULL: an empty value reads back as a result that fails to parse.</summary>
    [Test]
    public async Task ATraceThatEndsWithoutAResult_LeavesTheColumnNull()
    {
        ReadOnlyMemory<byte>? handedToTheStore = Array.Empty<byte>();
        using var failing = new ScriptedProcess();
        await using var failed = await LabHost.StartAsync(failing, configure: services => WrapStore(services, store => new FaultingStore(store)
        {
            OnComplete = completion => handedToTheStore = completion.ResultJson
        }));
        var failedStart = await failed.Service.StartExperimentAsync(failed.Request(), CancellationToken.None);
        failing.Finish([Handshake], exitCode: 139);
        await failed.Service.InFlight!.WaitAsync(TestBudgets.Contended);

        AssertEx.False(handedToTheStore.HasValue, "A null array must not reach the store as an empty result.");
        AssertEx.False((await failed.Reader.GetAsync(failedStart.ExperimentId!.Value, CancellationToken.None))!.ResultJson.HasValue, "A failed trace has no result.");

        using var cancelling = new ScriptedProcess();
        await using var cancelled = await LabHost.StartAsync(cancelling);
        var cancelledStart = await cancelled.Service.StartExperimentAsync(cancelled.Request(), CancellationToken.None);
        await cancelling.Spawned.WaitAsync(TestBudgets.Contended);
        AssertEx.True(cancelled.Service.Cancel(cancelledStart.ExperimentId!.Value));
        await cancelled.Service.InFlight!.WaitAsync(TestBudgets.Contended);

        AssertEx.False((await cancelled.Reader.GetAsync(cancelledStart.ExperimentId.Value, CancellationToken.None))!.ResultJson.HasValue, "A cancelled trace has no result.");
    }

    /// <summary>Without its last event a following page waits on a job that has already let go of the GPU.</summary>
    [Test]
    public async Task ACompletionThatCannotBeRecorded_StillEndsTheRunsEvents_AsFailed()
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process, configure: services => WrapStore(services, static store => new FaultingStore(store)
        {
            CompleteFailure = new InvalidOperationException("database is locked")
        }));
        var buffer = host.Services.GetRequiredService<ITrainingRunEventBuffer>();
        string? state = null;
        var start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);
        var id = start.ExperimentId!.Value;
        buffer.EventPublished += (_, args) =>
        {
            if (args.Event.RunId == id && args.Event.Payload.State is { } published)
            {
                state = published;
            }
        };

        process.Finish(CompleteRun, exitCode: 0);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);

        AssertEx.Equal(nameof(ModelLabExperimentStatus.Failed), state!);
        AssertEx.Null(host.Gate.ExclusiveKind);
    }

    /// <summary>Between the row's commit and the job's task nothing would cancel it, and the job would run past the host's stop.</summary>
    [Test]
    public async Task HostShutdown_BeforeTheJobHasATask_CancelsIt_AndNoProcessStarts()
    {
        LabHost? labHost = null;
        Task? stop = null;
        var completing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var completionMayProceed = new ManualResetEventSlim();
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process, configure: services => WrapStore(services, store => new FaultingStore(store)
        {
            AfterCreate = () => stop = labHost!.Service.StopAsync(CancellationToken.None),
            OnComplete = _ =>
            {
                completing.TrySetResult();
                completionMayProceed.Wait(TestBudgets.Contended);
            }
        }));
        labHost = host;

        var start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);
        await completing.Task.WaitAsync(TestBudgets.Contended);
        await AssertEx.StaysIncompleteAsync(stop!, "The stop must wait for the job's row, or it is written through a host that may be gone.");
        completionMayProceed.Set();
        await stop!.WaitAsync(TestBudgets.Contended);

        // No wait on the job's task here: the stop returning is the claim.
        AssertEx.Null(process.Request, "A cancelled job spawns nothing.");
        AssertEx.Equal(ModelLabExperimentStatus.Cancelled, (await host.Reader.GetAsync(start.ExperimentId!.Value, CancellationToken.None))!.Status);
        AssertEx.Null(host.Gate.ExclusiveKind);
    }

    /// <summary>The lease can take minutes to come free. A stop must not sit that out, and must not leave a start behind it that commits a row after the host has gone.</summary>
    [Test]
    public async Task HostShutdown_DuringTheLeaseWait_EndsTheStart_AndLeavesNoRow()
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process);
        var held = await host.Supervisor.AcquireRuntimeMutationLeaseOrNullAsync(CancellationToken.None);
        try
        {
            var starting = host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);
            await AssertEx.EventuallyAsync(() => host.Service.ActiveBaseArtifactId == host.Request().BaseArtifactId, TestBudgets.Contended, "The start reached its admission.");

            await host.Service.StopAsync(CancellationToken.None).WaitAsync(TestBudgets.Contended);

            AssertEx.Null(host.Gate.ExclusiveKind, "The stop returns only once the start has given the gate back.");
            AssertEx.Null(host.Service.ActiveBaseArtifactId);
            var refused = await starting.WaitAsync(TestBudgets.Contended);
            AssertEx.Equal(ModelLabStartRefusal.Busy, refused.Refusal!.Value);
            AssertEx.Contains(refused.RefusalMessage!, "stopping", StringComparison.Ordinal);
            AssertEx.Empty(await host.Reader.ListAsync(CancellationToken.None));
            AssertEx.Null(process.Request);
        }
        finally
        {
            await held!.DisposeAsync();
        }
    }

    /// <summary>The lease can take minutes to come free, and a delete in that wait would pull the checkpoint from under the job.</summary>
    [Test]
    public async Task TheCheckpoint_IsProtectedFromTheGate_WhileTheStartWaitsForTheLease()
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process);
        var held = await host.Supervisor.AcquireRuntimeMutationLeaseOrNullAsync(CancellationToken.None);
        Task<ModelLabStart> starting;
        try
        {
            starting = host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);
            await AssertEx.EventuallyAsync(() => host.Gate.ExclusiveKind is not null, TestBudgets.Contended, "The start reached its lease wait.");

            await AssertEx.EventuallyAsync(() => host.Service.ActiveBaseArtifactId == host.Request().BaseArtifactId, TimeSpan.FromSeconds(10), "The checkpoint is named from the gate on.");
            AssertEx.False(starting.IsCompleted, "The start is still waiting for the lease.");
            AssertEx.Null(host.Service.ActiveExperimentId, "No row exists yet, so nothing may follow an id.");
        }
        finally
        {
            await held!.DisposeAsync();
        }

        AssertEx.True((await starting.WaitAsync(TestBudgets.Contended)).ExperimentId.HasValue);
        process.Finish(CompleteRun, exitCode: 0);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);
    }

    [Test]
    public async Task ACheckpointDeletedBeforeTheGateWasTaken_IsRefused_AfterTheGate()
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process, configure: GateWithHook.Install);
        ((GateWithHook)host.Gate).BeforeBegin = async () =>
        {
            await using var scope = host.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IBaseArtifactService>().DeleteAsync(host.Request().BaseArtifactId, CancellationToken.None);
        };

        var start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);

        AssertEx.Equal(ModelLabStartRefusal.CheckpointNotFound, start.Refusal!.Value);
        AssertEx.Null(process.Request);
        AssertEx.Null(host.Gate.ExclusiveKind, "The refusal gives the gate back.");
        AssertEx.Null(host.Service.ActiveBaseArtifactId);
    }

    /// <summary>A delete past its guards but before its row delete holds the checkpoint: the cold start that publishes after it is refused, and the delete lands.</summary>
    [Test]
    public async Task ACheckpointDeleteInFlight_RefusesAColdStartAtItsPublication_AndTheDeleteLands()
    {
        var rowDelete = new StallingArtifactStore.Stall();
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process, configure: services =>
        {
            GateWithHook.Install(services);
            StallingArtifactStore.Install(services, rowDelete);
        });
        Task<BaseArtifactDeleteOutcome>? deleting = null;
        ((GateWithHook)host.Gate).BeforeBegin = async () =>
        {
            deleting = Task.Run(async () =>
            {
                await using var scope = host.Services.CreateAsyncScope();
                return await scope.ServiceProvider.GetRequiredService<IBaseArtifactService>().DeleteAsync(host.Request().BaseArtifactId, CancellationToken.None);
            });
            await rowDelete.Entered.Task.WaitAsync(TestBudgets.Contended);
        };

        ModelLabStart start;
        try
        {
            start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);
        }
        finally
        {
            rowDelete.MayProceed.TrySetResult();
        }

        AssertEx.Equal(ModelLabStartRefusal.CheckpointNotFound, start.Refusal.GetValueOrDefault(), start.RefusalMessage ?? "The start was admitted under a delete in flight.");
        AssertEx.Equal(BaseArtifactDeleteOutcome.Deleted, await deleting!.WaitAsync(TestBudgets.Contended));
        AssertEx.Null(process.Request, "The refused start spawned nothing.");
        AssertEx.Null(host.Gate.ExclusiveKind, "The refusal gives the gate back.");
        AssertEx.Null(host.Service.ActiveBaseArtifactId);
        AssertEx.Empty(await host.Reader.ListAsync(CancellationToken.None));
    }

    /// <summary>A delete that fails after its guards still lets go of its checkpoint, so the next start runs.</summary>
    [Test]
    public async Task ACheckpointDeleteThatFails_LetsGoOfItsCheckpoint()
    {
        var rowDelete = new StallingArtifactStore.Stall
        {
            Failure = new InvalidOperationException("database is locked")
        };
        rowDelete.MayProceed.SetResult();
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process, configure: services => StallingArtifactStore.Install(services, rowDelete));

        await using (var scope = host.Services.CreateAsyncScope())
        {
            _ = await AssertEx.ThrowsAsync<InvalidOperationException>(() =>
                scope.ServiceProvider.GetRequiredService<IBaseArtifactService>().DeleteAsync(host.Request().BaseArtifactId, CancellationToken.None));
        }

        var start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);
        AssertEx.True(start.ExperimentId.HasValue, start.RefusalMessage ?? "The failed delete still holds the checkpoint.");
        process.Finish(CompleteRun, exitCode: 0);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);
    }

    [Test]
    public void TheFixedSpec_IsThreeLayersAndNeverBlockZero()
    {
        AssertEx.Equal("6,12,18", string.Join(',', ModelLabTraceSpec.LayersFor(24)));
        AssertEx.Equal("8,16,24", string.Join(',', ModelLabTraceSpec.LayersFor(32)));
    }

    [Test]
    public void AnUnreadableTrace_IsUnavailable()
    {
        var trace = ModelLabTraceSpec.ParseTrace(Encoding.UTF8.GetBytes("{not json}\n"));

        AssertEx.Equal(ModelLabTraceReadStatus.Unavailable, trace.Status);
    }

    [Test]
    public void AStoredResult_ReadsBackThroughOneReader_AndAnUnreadableOneIsNone()
    {
        var read = ModelLabTraceSpec.ReadResultOrNull("""{"promptTokens":3,"text":"hi"}"""u8.ToArray());

        AssertEx.Equal(expected: 3, read!.PromptTokens);
        AssertEx.Equal("hi", read.Text);
        AssertEx.Null(ModelLabTraceSpec.ReadResultOrNull("{not json}"u8.ToArray()));
    }

    private static TrainingRuntimeStatus RuntimeStatus(TrainingRuntimePhase phase) =>
        new()
        {
            Phase = phase,
            IsRunning = phase is not (TrainingRuntimePhase.Ready or TrainingRuntimePhase.Idle or TrainingRuntimePhase.Failed),
            Terminal = phase is TrainingRuntimePhase.Ready or TrainingRuntimePhase.Idle or TrainingRuntimePhase.Failed,
            LogLines = [],
            LogStartSequence = 0,
            SanitizedError = null,
            Installed = null,
            StartedAtUtc = null,
            CompletedAtUtc = null
        };

    private static object TraceBody(LabHost host, int[] layers) =>
        new
        {
            baseArtifactId = host.Request().BaseArtifactId,
            installedModelName = Gguf,
            prompt = Prompt,
            licenseConfirmed = true,
            layers
        };

    private static object TokenBody(LabHost host, int maxNewTokens) =>
        new
        {
            baseArtifactId = host.Request().BaseArtifactId,
            installedModelName = Gguf,
            prompt = Prompt,
            licenseConfirmed = true,
            maxNewTokens
        };

    private static BaseCheckpointManifest Manifest(string repoId, long weightBytes) =>
        new()
        {
            RepoId = repoId,
            Revision = "2fc06364715b967f1860aea9cf38778875588b17",
            License = "apache-2.0",
            IsGated = false,
            TotalBytes = weightBytes + 100,
            Files =
            [
                new BaseCheckpointFile
                {
                    Role = BaseCheckpointFileRole.Config,
                    FileName = "config.json",
                    SizeBytes = 100
                },
                new BaseCheckpointFile
                {
                    Role = BaseCheckpointFileRole.Weights,
                    FileName = "model.safetensors",
                    SizeBytes = weightBytes
                }
            ]
        };

    private static string SeedBlobScope(LabHost host, Guid id)
    {
        var scope = Path.Combine(host.DataRoot, "model-lab", "artifacts", id.ToString("N"));
        Directory.CreateDirectory(scope);
        File.WriteAllText(Path.Combine(scope, "x.blob"), "ciphertext");
        return scope;
    }
}
