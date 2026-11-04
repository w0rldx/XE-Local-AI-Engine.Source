namespace XE_Local_AI_Engine.Tests.ModelLab;

using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Capacity.Implementation;
using XE_Local_AI_Engine.Client.Services.ModelLab;
using XE_Local_AI_Engine.Client.Services.Training.BaseArtifacts;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.HuggingFace.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.Training.Contracts;
using XE_Local_AI_Engine.Tests.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The job on the node's real DI: real GPU gate, supervisor lease, footprint ledger, capacity gate, store and blob store. Only the
///     process, the runtime's install state, the card and the GGUF header are scripted.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class ModelLabExperimentServiceTests
{
    internal const string Gguf = "org/Some-GGUF:Q4_K_M";
    private const long Card = 32_607L * 1024 * 1024;
    private const long WeightBytes = 1_746_942_600;
    private const string Prompt = "the secret prompt under test";

    internal static readonly string Handshake = $"{{\"event\":\"handshake\",\"contractVersion\":{ModelLabTraceSpec.ContractVersion}}}";
    internal static readonly string[] CompleteRun =
    [
        Handshake,
        """{"event":"phase","phase":"tracing"}""",
        """{"event":"progress","step":1,"totalSteps":128,"vramBytes":1000}""",
        """{"event":"done","cancelled":false}"""
    ];

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
        var record = AssertEx.NotNull(await host.Service.GetAsync(id, CancellationToken.None));
        AssertEx.Equal(ModelLabExperimentStatus.Succeeded, record.Status);
        AssertEx.Equal(Prompt, record.Prompt);
        AssertEx.False(Directory.Exists(host.WorkDirectory(id)), "No plaintext work directory survives the job.");
    }

    [Test]
    public async Task TheJobFile_CarriesTheFixedSpec_NotAnythingFromTheRequest()
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process);

        var start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);
        await process.Spawned.WaitAsync(TestBudgets.Contended);

        using var job = JsonDocument.Parse(process.ReadJobFile());
        var root = job.RootElement;
        AssertEx.Equal(ModelLabTraceSpec.ContractVersion, root.GetProperty("contractVersion").GetInt32());
        AssertEx.Equal(ModelLabTraceSpec.MaxNewTokens, root.GetProperty("maxNewTokens").GetInt32());
        AssertEx.Equal(ModelLabTraceSpec.TraceByteCap, root.GetProperty("traceByteCap").GetInt32());
        AssertEx.Equal("[6,12,18]", root.GetProperty("layers").GetRawText());
        AssertEx.Equal(Prompt, root.GetProperty("prompt").GetString());
        AssertEx.Equal("modellab.py", Path.GetFileName(process.Request!.Arguments[0]));
        if (OperatingSystem.IsLinux())
        {
            AssertEx.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(process.Request.Arguments[2]), "It holds the plaintext prompt.");
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
        using var process = new ScriptedProcess { TraceContent = record + "\n" + footer + "\n" };
        await using var host = await LabHost.StartAsync(process);
        var start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);
        process.Finish(CompleteRun, exitCode: 0);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);
        var id = start.ExperimentId!.Value;

        var trace = await host.Service.ReadTraceAsync(id, CancellationToken.None);

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

        var trace = await host.Service.ReadTraceAsync(id, CancellationToken.None);

        AssertEx.Equal(ModelLabTraceReadStatus.Unavailable, trace.Status);
    }

    [Test]
    public async Task ATraceOverItsByteCap_IsNotKept_EvenWhenTheScriptClaimsSuccess()
    {
        using var process = new ScriptedProcess { TraceContent = new string('x', ModelLabTraceSpec.TraceByteCap + 1) };
        await using var host = await LabHost.StartAsync(process);
        var start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);
        process.Finish(CompleteRun, exitCode: 0);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);
        var id = start.ExperimentId!.Value;

        var record = AssertEx.NotNull(await host.Service.GetAsync(id, CancellationToken.None));

        AssertEx.Equal(ModelLabExperimentStatus.Failed, record.Status);
        AssertEx.True(record.ErrorMessage!.Contains("byte cap", StringComparison.Ordinal), record.ErrorMessage);
        AssertEx.Null(record.TraceBlobId);
        AssertEx.False(Directory.Exists(Path.Combine(host.DataRoot, "model-lab", "artifacts", id.ToString("N"))), "Nothing over the cap reaches the store.");
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
        var record = AssertEx.NotNull(await host.Service.GetAsync(id, CancellationToken.None));
        AssertEx.Equal(ModelLabExperimentStatus.Cancelled, record.Status);
        AssertEx.Null(record.TraceBlobId);
        AssertEx.Null(host.Gate.ExclusiveKind);
        AssertEx.False(Directory.Exists(host.WorkDirectory(id)));
        AssertEx.False(host.Service.Cancel(id), "A finished job has nothing to cancel.");
    }

    [Test]
    public async Task Cancel_KillsAScriptThatIgnoresTheStop_AndTheOutcomeIsStillCancelled()
    {
        using var process = new ScriptedProcess { IgnoresStop = true };
        await using var host = await LabHost.StartAsync(process, new ModelLabJobOptions { ExitGracePeriod = TimeSpan.FromMilliseconds(50) });
        var start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);
        await process.Spawned.WaitAsync(TestBudgets.Contended);
        var id = start.ExperimentId!.Value;

        AssertEx.True(host.Service.Cancel(id));
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);

        AssertEx.True(process.Killed, "A wedged script cannot honour a cooperative stop.");
        AssertEx.Equal(ModelLabExperimentStatus.Cancelled, (await host.Service.GetAsync(id, CancellationToken.None))!.Status);
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

        var record = AssertEx.NotNull(await host.Service.GetAsync(start.ExperimentId!.Value, CancellationToken.None));

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

        var record = AssertEx.NotNull(await host.Service.GetAsync(start.ExperimentId!.Value, CancellationToken.None));

        AssertEx.Equal("The GPU ran out of memory during the trace.", record.ErrorMessage!);
    }

    [Test]
    public async Task AFailedRow_KeepsTheLastLinesTheScriptPrinted_BecauseANativeToolExplainsItselfOnlyThere()
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process);
        var start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);
        var noise = Enumerable.Range(0, 30).Select(static i => $"noise {i}");
        process.Finish([Handshake, .. noise, "cuda_utils.c:9:10: fatal error: Python.h: No such file or directory", """{"event":"error","category":"trace","message":"CalledProcessError"}"""], exitCode: 1);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);

        var record = AssertEx.NotNull(await host.Service.GetAsync(start.ExperimentId!.Value, CancellationToken.None));

        AssertEx.True(record.ErrorMessage!.StartsWith("CalledProcessError", StringComparison.Ordinal), record.ErrorMessage);
        AssertEx.True(record.ErrorMessage.Contains("Python.h", StringComparison.Ordinal), record.ErrorMessage);
        AssertEx.False(record.ErrorMessage.Contains("noise 0\n", StringComparison.Ordinal), "The tail is bounded.");
    }

    [Test]
    public async Task AnotherProtocolVersion_IsKilledAtTheHandshake()
    {
        using var process = new ScriptedProcess { IgnoresStop = true };
        await using var host = await LabHost.StartAsync(process);
        var start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);
        process.Say($"{{\"event\":\"handshake\",\"contractVersion\":{ModelLabTraceSpec.ContractVersion + 1}}}");
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);

        AssertEx.True(process.Killed);
        var record = AssertEx.NotNull(await host.Service.GetAsync(start.ExperimentId!.Value, CancellationToken.None));
        AssertEx.True(record.ErrorMessage!.Contains("protocol version", StringComparison.Ordinal), record.ErrorMessage);
    }

    [Test]
    public async Task ASilentScript_IsKilledByTheWatchdog()
    {
        using var process = new ScriptedProcess { IgnoresStop = true };
        await using var host = await LabHost.StartAsync(process, new ModelLabJobOptions { InactivityTimeout = TimeSpan.FromMilliseconds(50) });
        var start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);

        AssertEx.True(process.Killed);
        var record = AssertEx.NotNull(await host.Service.GetAsync(start.ExperimentId!.Value, CancellationToken.None));
        AssertEx.Equal(ModelLabExperimentStatus.Failed, record.Status);
        AssertEx.True(record.ErrorMessage!.Contains("stopped reporting", StringComparison.Ordinal), record.ErrorMessage);
    }

    [Test]
    public async Task ProgressAndTheEnd_ReachTheRunBuffer_UnderTheExperimentsId()
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process);
        var seen = new List<XE_Local_AI_Engine.Client.Services.Training.Runs.TrainingRunEvent>();
        var buffer = host.Services.GetRequiredService<XE_Local_AI_Engine.Client.Services.Training.Runs.ITrainingRunEventBuffer>();
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
        List<XE_Local_AI_Engine.Client.Services.Training.Runs.TrainingRunEvent> events;
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
        AssertEx.Equal(expected: 0, (await host.Service.ListAsync(CancellationToken.None)).Count, "A refusal writes nothing.");
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

        var unknown = await host.Service.StartExperimentAsync(host.Request() with { BaseArtifactId = Guid.NewGuid() }, CancellationToken.None);
        AssertEx.Equal(ModelLabStartRefusal.CheckpointNotFound, unknown.Refusal!.Value);

        var unconfirmed = await host.Service.StartExperimentAsync(host.Request() with { LicenseConfirmed = false }, CancellationToken.None);
        AssertEx.Equal(ModelLabStartRefusal.LicenseNotConfirmed, unconfirmed.Refusal!.Value);

        var uninstalled = await host.Service.StartExperimentAsync(host.Request() with { InstalledModelName = "org/Other-GGUF:Q4_K_M" }, CancellationToken.None);
        AssertEx.Equal(ModelLabStartRefusal.PreflightRefused, uninstalled.Refusal!.Value);

        var notReady = await host.Service.StartExperimentAsync(host.Request() with { BaseArtifactId = await host.SeedDownloadingArtifactAsync() }, CancellationToken.None);
        AssertEx.Equal(ModelLabStartRefusal.CheckpointNotReady, notReady.Refusal!.Value);

        host.Runtime.ResolveInterpreterPath().Returns((string?)null);
        var noRuntime = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);
        AssertEx.Equal(ModelLabStartRefusal.RuntimeUnavailable, noRuntime.Refusal!.Value);

        AssertEx.Null(process.Request);
        AssertEx.Equal(expected: 0, (await host.Service.ListAsync(CancellationToken.None)).Count);
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
        host.Checkpoints.DownloadAsync(Arg.Any<BaseCheckpointManifest>(), Arg.Any<string>(), Arg.Any<IProgress<XE_Local_AI_Engine.Providers.Abstractions.Contracts.PullProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var manifest = call.Arg<BaseCheckpointManifest>();
                var path = Path.Combine(call.Arg<string>(), "config.json");
                File.WriteAllText(path, LabHost.Config);
                return manifest with { Files = [.. manifest.Files.Select(file => file with { LocalPath = path })] };
            });

        var refused = await host.Service.StartCheckpointDownloadAsync("Qwen/Qwen3.5-0.8B", revision: null, Gguf, licenseConfirmed: false, CancellationToken.None);

        AssertEx.Equal(ModelLabCheckpointRefusal.LicenseNotConfirmed, refused.Preflight.Refusal!.Value);
        AssertEx.Null(refused.BaseArtifactId);
        AssertEx.True(refused.Preflight.RefusalMessage!.Contains("declares the license 'apache-2.0'", StringComparison.Ordinal), refused.Preflight.RefusalMessage);
        await host.Checkpoints.DidNotReceive().DownloadAsync(Arg.Is<BaseCheckpointManifest>(manifest => manifest.Files.Any(file => file.Role == BaseCheckpointFileRole.Weights)),
            Arg.Any<string>(),
            Arg.Any<IProgress<XE_Local_AI_Engine.Providers.Abstractions.Contracts.PullProgress>?>(),
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

        var rows = await host.Service.ListAsync(CancellationToken.None);
        AssertEx.Equal(ModelLabExperimentService.MaxRetainedExperiments, rows.Count);
        AssertEx.False(rows.Any(row => row.Id == oldest), "The oldest row went.");
        AssertEx.True(rows.Any(row => row.Id == start.ExperimentId), "The new one stayed.");
        AssertEx.False(Directory.Exists(oldestScope), "Its blobs went with it.");
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
            AssertEx.Equal(System.Net.HttpStatusCode.Conflict, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            AssertEx.Equal("in-use-by-experiment", body.RootElement.GetProperty("reason").GetString());
            AssertEx.True(body.RootElement.GetProperty("message").GetString()!.Contains("Model Lab", StringComparison.Ordinal));
        }

        process.Finish(CompleteRun, exitCode: 0);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);

        using (var request = host.Authorized(HttpMethod.Delete, artifactRoute))
        using (var response = await client.SendAsync(request))
        {
            AssertEx.Equal(System.Net.HttpStatusCode.NoContent, response.StatusCode);
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
        AssertEx.Equal(System.Net.HttpStatusCode.NoContent, response.StatusCode);
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
            await scope.ServiceProvider.GetRequiredService<IModelLabExperimentStore>().SetLaunchReceiptAsync(id, new byte[] { 1 });
        }

        using var client = host.Factory.CreateClient();
        using var request = host.Authorized(HttpMethod.Delete, $"/api/local/v1/model-lab/experiments/{id}");
        using var response = await client.SendAsync(request);

        AssertEx.Equal(System.Net.HttpStatusCode.Conflict, response.StatusCode);
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
        var buffer = host.Services.GetRequiredService<XE_Local_AI_Engine.Client.Services.Training.Runs.ITrainingRunEventBuffer>();
        string? state = null;
        Guid? activeAtEvent = Guid.NewGuid();
        GpuWorkKind? gateAtEvent = GpuWorkKind.ImageJob;
        var workDirectoryAtEvent = true;
        var start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);
        var id = start.ExperimentId!.Value;
        buffer.EventPublished += (_, args) =>
        {
            if (args.Event.RunId == id && args.Event.Payload.State is { } published)
            {
                state = published;
                activeAtEvent = host.Service.ActiveExperimentId;
                gateAtEvent = host.Gate.ExclusiveKind;
                workDirectoryAtEvent = Directory.Exists(host.WorkDirectory(id));
            }
        };

        process.Finish(exitCode == 0 ? CompleteRun : [Handshake], exitCode);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);

        AssertEx.Equal(expectedState, state!);
        AssertEx.Null(activeAtEvent, "A list read after the event must not name the finished job.");
        AssertEx.Null(gateAtEvent, "A start right after the event must be admitted.");
        AssertEx.False(workDirectoryAtEvent, "The plaintext goes before the job announces its end.");
        AssertEx.True(buffer.Replay(id, afterSequence: 0).ResetRequired, "The buffer is still evicted, after the last event.");
    }

    [Test]
    public async Task TheFinalStateEvent_FollowsACancel_AfterTheJobHasLetGo()
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process);
        var buffer = host.Services.GetRequiredService<XE_Local_AI_Engine.Client.Services.Training.Runs.ITrainingRunEventBuffer>();
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
        AssertEx.Equal(ModelLabExperimentStatus.Cancelled, (await host.Service.GetAsync(start.ExperimentId!.Value, CancellationToken.None))!.Status);
        AssertEx.Null(host.Gate.ExclusiveKind);
    }

    [Test]
    public async Task ARequestAbortedRightAfterTheRowCommits_StillRunsTheJob()
    {
        using var aborted = new CancellationTokenSource();
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process, configure: services => WrapStore(services, store => new FaultingStore(store) { AfterCreate = aborted.Cancel }));

        var start = await host.Service.StartExperimentAsync(host.Request(), aborted.Token);

        AssertEx.True(start.ExperimentId.HasValue, start.RefusalMessage ?? "The job should have started.");
        process.Finish(CompleteRun, exitCode: 0);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);
        AssertEx.Equal(ModelLabExperimentStatus.Succeeded, (await host.Service.GetAsync(start.ExperimentId!.Value, CancellationToken.None))!.Status);
    }

    [Test]
    public async Task AFailedTrim_NeitherFailsTheStart_NorStrandsTheRow()
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process, configure: services => WrapStore(services, static store => new FaultingStore(store) { TrimFailure = new InvalidOperationException("database is locked") }));

        var start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);

        AssertEx.True(start.ExperimentId.HasValue, start.RefusalMessage ?? "The job should have started.");
        process.Finish(CompleteRun, exitCode: 0);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);
        AssertEx.Equal(ModelLabExperimentStatus.Succeeded, (await host.Service.GetAsync(start.ExperimentId!.Value, CancellationToken.None))!.Status);
        AssertEx.Null(host.Gate.ExclusiveKind);
    }

    [Test]
    public async Task ATraceWithNonFiniteValues_ReadsBackThroughTheRoute_WithNulls()
    {
        const string record = """{"s":0,"l":6,"k":"F","n":null,"p":[null,0.0,1.0],"ti":[7,8],"tv":[null,-0.25]}""";
        const string footer = """{"footer":true,"truncated":false,"records":1,"dropped":0,"payloadBytes":10,"cap":262144}""";
        using var process = new ScriptedProcess { TraceContent = record + "\n" + footer + "\n" };
        await using var host = await LabHost.StartAsync(process);
        var start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);
        process.Finish(CompleteRun, exitCode: 0);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);

        using var client = host.Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/local/v1/model-lab/experiments/{start.ExperimentId}/trace");
        host.Factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");
        using var response = await client.SendAsync(request);

        AssertEx.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
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
            Content = JsonContent.Create(new { baseArtifactId = host.Request().BaseArtifactId, installedModelName = Gguf })
        };
        host.Factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");
        using var response = await client.SendAsync(request);

        AssertEx.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
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
        AssertEx.False((await failed.Service.GetAsync(failedStart.ExperimentId!.Value, CancellationToken.None))!.ResultJson.HasValue, "A failed trace has no result.");

        using var cancelling = new ScriptedProcess();
        await using var cancelled = await LabHost.StartAsync(cancelling);
        var cancelledStart = await cancelled.Service.StartExperimentAsync(cancelled.Request(), CancellationToken.None);
        await cancelling.Spawned.WaitAsync(TestBudgets.Contended);
        AssertEx.True(cancelled.Service.Cancel(cancelledStart.ExperimentId!.Value));
        await cancelled.Service.InFlight!.WaitAsync(TestBudgets.Contended);

        AssertEx.False((await cancelled.Service.GetAsync(cancelledStart.ExperimentId.Value, CancellationToken.None))!.ResultJson.HasValue, "A cancelled trace has no result.");
    }

    /// <summary>Without its last event a following page waits on a job that has already let go of the GPU.</summary>
    [Test]
    public async Task ACompletionThatCannotBeRecorded_StillEndsTheRunsEvents_AsFailed()
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process, configure: services => WrapStore(services, static store => new FaultingStore(store) { CompleteFailure = new InvalidOperationException("database is locked") }));
        var buffer = host.Services.GetRequiredService<XE_Local_AI_Engine.Client.Services.Training.Runs.ITrainingRunEventBuffer>();
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
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process, configure: services => WrapStore(services, store => new FaultingStore(store)
        {
            AfterCreate = () => stop = labHost!.Service.StopAsync(CancellationToken.None)
        }));
        labHost = host;

        var start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);
        await stop!.WaitAsync(TestBudgets.Contended);
        await host.Service.InFlight!.WaitAsync(TimeSpan.FromSeconds(20));

        AssertEx.Null(process.Request, "A cancelled job spawns nothing.");
        AssertEx.Equal(ModelLabExperimentStatus.Cancelled, (await host.Service.GetAsync(start.ExperimentId!.Value, CancellationToken.None))!.Status);
        AssertEx.Null(host.Gate.ExclusiveKind);
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
        await using var host = await LabHost.StartAsync(process, configure: static services =>
        {
            services.RemoveAll<IGpuWorkGate>();
            services.AddSingleton<IGpuWorkGate>(static provider => new GateWithHook(ActivatorUtilities.CreateInstance<GpuWorkGate>(provider)));
        });
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

    [Test]
    public void TheFixedSpec_IsThreeLayersAndNeverBlockZero()
    {
        AssertEx.Equal("6,12,18", string.Join(',', ModelLabTraceSpec.LayersFor(24)));
        AssertEx.Equal("8,16,24", string.Join(',', ModelLabTraceSpec.LayersFor(32)));
    }

    [Test]
    public void AnUnreadableTrace_IsUnavailable()
    {
        var trace = ModelLabExperimentService.ParseTrace(Encoding.UTF8.GetBytes("{not json}\n"));

        AssertEx.Equal(ModelLabTraceReadStatus.Unavailable, trace.Status);
    }

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
                new BaseCheckpointFile { Role = BaseCheckpointFileRole.Config, FileName = "config.json", SizeBytes = 100 },
                new BaseCheckpointFile { Role = BaseCheckpointFileRole.Weights, FileName = "model.safetensors", SizeBytes = weightBytes }
            ]
        };

    internal static void WrapStore(IServiceCollection services, Func<IModelLabExperimentStore, IModelLabExperimentStore> wrap)
    {
        services.RemoveAll<IModelLabExperimentStore>();
        services.AddScoped(provider => wrap(ActivatorUtilities.CreateInstance<ModelLabExperimentStore>(provider)));
    }

    /// <summary>The real store, failing only where the test says, so a commit that happened really happened.</summary>
    internal sealed class FaultingStore : IModelLabExperimentStore
    {
        private readonly IModelLabExperimentStore _inner;
        private Guid _created;

        public FaultingStore(IModelLabExperimentStore inner)
        {
            _inner = inner;
        }

        public Action? BeforeCreate { get; init; }

        public Func<Guid, Task>? BeforeTrim { get; init; }

        public Action? AfterCreate { get; init; }

        public Exception? TrimFailure { get; init; }

        public Exception? CompleteFailure { get; init; }

        public Action? BeforeProgress { get; init; }

        public Action<ModelLabExperimentCompletion>? OnComplete { get; init; }

        public async Task CreateRunningAsync(ModelLabExperimentCreate command, CancellationToken cancellationToken = default)
        {
            BeforeCreate?.Invoke();
            await _inner.CreateRunningAsync(command, cancellationToken);
            _created = command.Id;
            AfterCreate?.Invoke();
        }

        public async Task<IReadOnlyList<Guid>> TrimToNewestAsync(int keep, CancellationToken cancellationToken = default)
        {
            if (BeforeTrim is not null)
            {
                await BeforeTrim(_created);
            }

            return TrimFailure is null ? await _inner.TrimToNewestAsync(keep, cancellationToken) : throw TrimFailure;
        }

        public Task<ModelLabExperimentRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default) => _inner.GetAsync(id, cancellationToken);

        public Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken = default) => _inner.ExistsAsync(id, cancellationToken);

        public Task<IReadOnlyList<ModelLabExperimentRecord>> ListAsync(CancellationToken cancellationToken = default) => _inner.ListAsync(cancellationToken);

        public Task SetLaunchReceiptAsync(Guid id, ReadOnlyMemory<byte>? launchReceiptJson, CancellationToken cancellationToken = default) => _inner.SetLaunchReceiptAsync(id, launchReceiptJson, cancellationToken);

        public Task SetProgressAsync(Guid id, ReadOnlyMemory<byte> resultJson, ModelLabBlobReference? blob, CancellationToken cancellationToken = default)
        {
            BeforeProgress?.Invoke();
            return _inner.SetProgressAsync(id, resultJson, blob, cancellationToken);
        }

        public Task CompleteAsync(Guid id, ModelLabExperimentCompletion completion, CancellationToken cancellationToken = default)
        {
            OnComplete?.Invoke(completion);
            return CompleteFailure is null ? _inner.CompleteAsync(id, completion, cancellationToken) : throw CompleteFailure;
        }

        public Task<ModelLabExperimentDeleteOutcome> DeleteAsync(Guid id, CancellationToken cancellationToken = default) => _inner.DeleteAsync(id, cancellationToken);

        public Task<IReadOnlyList<ModelLabLaunchReceipt>> ListLaunchReceiptsAsync(CancellationToken cancellationToken = default) => _inner.ListLaunchReceiptsAsync(cancellationToken);

        public Task<IReadOnlyList<Guid>> RecoverOnStartupAsync(CancellationToken cancellationToken = default) => _inner.RecoverOnStartupAsync(cancellationToken);
    }

    /// <summary>The real gate, with a step just before it is taken: the last moment a delete can pass the guard.</summary>
    private sealed class GateWithHook : IGpuWorkGate
    {
        private readonly IGpuWorkGate _inner;

        public GateWithHook(IGpuWorkGate inner)
        {
            _inner = inner;
        }

        public Func<Task>? BeforeBegin { get; set; }

        public GpuWorkKind? ExclusiveKind => _inner.ExclusiveKind;

        public IReadOnlyList<GpuWorkKind> Holders => _inner.Holders;

        public bool TryBeginExclusive(GpuWorkKind kind, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IDisposable? handle)
        {
            BeforeBegin?.Invoke().GetAwaiter().GetResult();
            return _inner.TryBeginExclusive(kind, out handle);
        }

        public bool TryBeginShared(GpuWorkKind kind, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IDisposable? handle) =>
            _inner.TryBeginShared(kind, out handle);
    }

    /// <summary>A real test host, with one artifact Ready and an installed GGUF that pairs with it.</summary>
    internal sealed class LabHost : IAsyncDisposable
    {
        public const string Config = """{"model_type":"qwen3_5","text_config":{"hidden_size":1024,"num_hidden_layers":24,"layer_types":["linear_attention","linear_attention","linear_attention","full_attention","linear_attention","linear_attention","linear_attention","full_attention","linear_attention","linear_attention","linear_attention","full_attention","linear_attention","linear_attention","linear_attention","full_attention","linear_attention","linear_attention","linear_attention","full_attention","linear_attention","linear_attention","linear_attention","full_attention"]}}""";

        private readonly TestServerWebAppFactory _factory;
        private readonly Guid _artifactId;

        private LabHost(TestServerWebAppFactory factory, Guid artifactId, ITrainingRuntimeService runtime, IBaseCheckpointStore checkpoints)
        {
            _factory = factory;
            _artifactId = artifactId;
            Runtime = runtime;
            Checkpoints = checkpoints;
        }

        public ITrainingRuntimeService Runtime { get; }

        public IBaseCheckpointStore Checkpoints { get; }

        public TestServerWebAppFactory Factory => _factory;

        public IServiceProvider Services => _factory.Services;

        public ModelLabExperimentService Service => Services.GetRequiredService<ModelLabExperimentService>();

        public IGpuWorkGate Gate => Services.GetRequiredService<IGpuWorkGate>();

        public IPendingFootprintLedger Ledger => Services.GetRequiredService<IPendingFootprintLedger>();

        public ILlamaServerProcessSupervisor Supervisor => Services.GetRequiredService<ILlamaServerProcessSupervisor>();

        public string DataRoot => Services.GetRequiredService<INodeDataDirectory>().Root;

        public string WorkDirectory(Guid id) =>
            Path.Combine(ModelLabExperimentService.WorkRoot(Services.GetRequiredService<INodeDataDirectory>()), id.ToString("N"));

        public static async Task<LabHost> StartAsync(ScriptedProcess process,
            ModelLabJobOptions? options = null,
            ILlamaServerProcessSupervisor? supervisor = null,
            long availableVramBytes = Card,
            Action<IServiceCollection>? configure = null)
        {
            var interpreter = Path.Combine(Path.GetTempPath(), "python-" + Guid.NewGuid().ToString("N"));
            var runtime = Substitute.For<ITrainingRuntimeService>();
            runtime.ResolveInterpreterPath().Returns(interpreter);
            runtime.GetStatus().Returns(new TrainingRuntimeStatus
            {
                Phase = TrainingRuntimePhase.Ready,
                IsRunning = false,
                Terminal = true,
                LogLines = [],
                LogStartSequence = 0,
                SanitizedError = null,
                Installed = null,
                StartedAtUtc = null,
                CompletedAtUtc = null
            });

            var audit = Substitute.For<IRuntimeDeviceAudit>();
            audit.GetEffectiveProfileAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(new HardwareProfile
            {
                TotalRamBytes = long.MaxValue / 4,
                AvailableRamBytes = long.MaxValue / 4,
                VramBytes = Card,
                AvailableVramBytes = availableVramBytes,
                VramKnown = true,
                GpuVendor = GpuVendor.Nvidia,
                GpuAccelAvailable = true,
                CpuCores = 8,
                FreeDiskBytes = long.MaxValue / 4
            });

            var ggufStore = Substitute.For<IGgufModelStore>();
            ggufStore.ResolveModelFilePathAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null);
            ggufStore.ResolveModelFilePathAsync(Gguf, Arg.Any<CancellationToken>()).Returns("/models/some.gguf");
            var ggufReader = Substitute.For<IGgufMetadataReader>();
            ggufReader.ReadTensorsAsync("/models/some.gguf", Arg.Any<CancellationToken>()).Returns(new GgufTensorInspection
            {
                Failure = GgufTensorReadFailure.None,
                Version = 3,
                Architecture = "qwen35",
                ModelName = null,
                ParamCount = null,
                FileType = null,
                BlockCount = 24,
                NextnPredictLayers = null,
                Alignment = null,
                AttentionHeadCount = null,
                AttentionHeadCountKv = null,
                EmbeddingLength = 1024,
                ContextLength = null,
                ExpertCount = null,
                ChatTemplate = null,
                SplitCount = null,
                TensorCount = 0,
                Tensors = [],
                HeaderBytes = 0,
                FileSizeBytes = 0
            });

            var checkpoints = Substitute.For<IBaseCheckpointStore>();
#pragma warning disable CA2000 // LabHost owns it.
            var factory = new TestServerWebAppFactory
            {
                ConfigureAdditionalTestServices = services =>
                {
                    services.RemoveAll<ITrainingProcessSpawner>();
                    services.AddSingleton<ITrainingProcessSpawner>(process);
                    services.RemoveAll<ITrainingRuntimeService>();
                    services.AddSingleton(runtime);
                    services.RemoveAll<IRuntimeDeviceAudit>();
                    services.AddSingleton(audit);
                    services.RemoveAll<IGgufModelStore>();
                    services.AddSingleton(ggufStore);
                    services.RemoveAll<IGgufMetadataReader>();
                    services.AddSingleton(ggufReader);
                    services.RemoveAll<IBaseCheckpointStore>();
                    services.AddSingleton(checkpoints);
                    if (options is not null)
                    {
                        services.RemoveAll<IOptions<ModelLabJobOptions>>();
                        services.AddSingleton(Options.Create(options));
                    }

                    if (supervisor is not null)
                    {
                        services.RemoveAll<ILlamaServerProcessSupervisor>();
                        services.AddSingleton(supervisor);
                    }

                    configure?.Invoke(services);
                }
            };
#pragma warning restore CA2000
            _ = factory.Server;

            var artifactId = await SeedReadyArtifactAsync(factory.Services);
            return new LabHost(factory, artifactId, runtime, checkpoints);
        }

        public ModelLabExperimentRequest Request() =>
            new()
            {
                BaseArtifactId = _artifactId,
                InstalledModelName = Gguf,
                Prompt = Prompt,
                LicenseConfirmed = true
            };

        public HttpRequestMessage Authorized(HttpMethod method, string path)
        {
            var request = new HttpRequestMessage(method, path);
            _factory.AddNodeBearerToken(request);
            request.Headers.Add("Origin", "http://localhost");
            return request;
        }

        public async Task SeedRunningAsync(Guid id)
        {
            await using var scope = Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IModelLabExperimentStore>().CreateRunningAsync(new ModelLabExperimentCreate
            {
                Id = id,
                RepoId = "Qwen/Qwen3.5-0.8B",
                Revision = "rev",
                BaseArtifactId = _artifactId,
                InstalledModelName = Gguf,
                Prompt = "seed",
                LicenseConfirmationJson = new byte[] { 1 }
            });
        }

        public async Task SeedFinishedAsync(Guid id)
        {
            await SeedRunningAsync(id);
            await using var scope = Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IModelLabExperimentStore>().CompleteAsync(id, new ModelLabExperimentCompletion { Status = ModelLabExperimentStatus.Failed, ErrorMessage = "seed" });
        }

        public async Task<Guid> SeedDownloadingArtifactAsync()
        {
            await using var scope = Services.CreateAsyncScope();
            var record = await scope.ServiceProvider.GetRequiredService<ITrainingBaseArtifactStore>().StartDownloadAsync("Qwen/Qwen3.5-9B", "rev");
            return record.Id;
        }

        public async ValueTask DisposeAsync() =>
            await _factory.DisposeAsync();

        private static async Task<Guid> SeedReadyArtifactAsync(IServiceProvider services)
        {
            await using var scope = services.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<ITrainingBaseArtifactStore>();
            var record = await store.StartDownloadAsync("Qwen/Qwen3.5-0.8B", "2fc06364715b967f1860aea9cf38778875588b17");
            var directory = Path.Combine(services.GetRequiredService<INodeDataDirectory>().Root, "training", "base", record.Id.ToString());
            Directory.CreateDirectory(directory);
            var configPath = Path.Combine(directory, "config.json");
            await File.WriteAllTextAsync(configPath, Config);
            var files = JsonSerializer.SerializeToUtf8Bytes(new[]
            {
                new BaseArtifactFileView("Config", "config.json", configPath, 100, null),
                new BaseArtifactFileView("Weights", "model.safetensors", Path.Combine(directory, "model.safetensors"), WeightBytes, null)
            });
            var license = JsonSerializer.SerializeToUtf8Bytes(new BaseArtifactLicenseView("Qwen/Qwen3.5-0.8B", "apache-2.0", IsGated: false, DateTimeOffset.UnixEpoch));
            _ = await store.MarkReadyAsync(record.Id, record.Version, files, WeightBytes + 100, license);
            return record.Id;
        }
    }

    /// <summary>A scripted <c>modellab.py</c>: it stays running until the test finishes it, and writes the files the job file names.</summary>
    internal sealed class ScriptedProcess : ITrainingProcessSpawner, ITrainingProcessHandle
    {
        private readonly TaskCompletionSource _spawned = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Channel<string> _output = Channel.CreateUnbounded<string>();

        public string TraceContent { get; init; } = "{\"footer\":true,\"truncated\":false,\"records\":0,\"dropped\":0,\"payloadBytes\":0,\"cap\":262144}\n";

        public byte[] VectorContent { get; init; } = "the-steering-vector-bytes"u8.ToArray();

        public byte[] RandomVectorContent { get; init; } = "the-random-vector-bytes"u8.ToArray();

        public string DerivedResult { get; init; } = """{"seconds":1.5,"peakVramBytes":10,"blockCount":24,"steering":{"layer":15,"layerKind":"F","referenceNorm":3.9,"differenceNorm":0.8,"vectorNorm":1.95,"randomSeed":1234,"loadSeconds":2.5}}""";

        public bool IgnoresStop { get; init; }

        public Task Spawned => _spawned.Task;

        public TrainingSpawnRequest? Request { get; private set; }

        public volatile bool StopRequested;

        public volatile bool Killed;

        public TrainingLaunchReceipt Receipt { get; } = new()
        {
            Pid = 4242,
            Pgid = 4242,
            ExecutablePath = "/venv/bin/python",
            StartTicks = 1,
            RunToken = "token"
        };

        public ITrainingProcessHandle Spawn(TrainingSpawnRequest request)
        {
            Request = request;
            _spawned.TrySetResult();
            return this;
        }

        public string ReadJobFile() =>
            File.ReadAllText(Request!.Arguments[2]);

        public void Say(string line) =>
            _output.Writer.TryWrite(line);

        /// <summary>Waits for the spawn, writes the result files as the script would, then says the lines and exits.</summary>
        public void Finish(IReadOnlyList<string> lines, int exitCode) =>
            _ = FinishAsync(lines, exitCode);

        public IAsyncEnumerable<string> ReadOutputAsync(CancellationToken cancellationToken) =>
            ReadAsync(cancellationToken);

        public Task<int> WaitForExitAsync(CancellationToken cancellationToken) =>
            _exit.Task.WaitAsync(cancellationToken);

        public void RequestStop()
        {
            StopRequested = true;
            if (!IgnoresStop)
            {
                End(exitCode: 3);
            }
        }

        public void KillGroup()
        {
            Killed = true;
            End(exitCode: 137);
        }

        public void Dispose()
        {
            End(exitCode: 137);
        }

        private async Task FinishAsync(IReadOnlyList<string> lines, int exitCode)
        {
            await Spawned;
            using var job = JsonDocument.Parse(ReadJobFile());
            if (job.RootElement.TryGetProperty("vectorPath", out var vectorPath))
            {
                await File.WriteAllBytesAsync(vectorPath.GetString()!, VectorContent);
                await File.WriteAllBytesAsync(job.RootElement.GetProperty("randomVectorPath").GetString()!, RandomVectorContent);
                await File.WriteAllTextAsync(job.RootElement.GetProperty("resultPath").GetString()!, DerivedResult);
            }
            else
            {
                await File.WriteAllTextAsync(job.RootElement.GetProperty("tracePath").GetString()!, TraceContent);
                await File.WriteAllTextAsync(job.RootElement.GetProperty("resultPath").GetString()!, """{"promptTokens":3,"tokens":[{"id":9,"text":"hi"}],"text":"hi","seconds":0.5,"peakVramBytes":10,"blockCount":24}""");
            }

            foreach (var line in lines)
            {
                Say(line);
            }

            End(exitCode);
        }

        private void End(int exitCode)
        {
            _output.Writer.TryComplete();
            _exit.TrySetResult(exitCode);
        }

        private async IAsyncEnumerable<string> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var line in _output.Reader.ReadAllAsync(cancellationToken))
            {
                yield return line;
            }
        }
    }
}
