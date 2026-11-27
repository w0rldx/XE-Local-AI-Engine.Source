namespace XE_Local_AI_Engine.Tests.ModelLab;

using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Endpoints.ModelLab.V1;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.ModelLab;
using XE_Local_AI_Engine.Client.Services.Training.Runs;
using XE_Local_AI_Engine.Client.Services.Training.Runtime;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Training.Contracts;
using XE_Local_AI_Engine.Tests.Testing;
using static ModelLabPlaygroundContractTests;

/// <summary>
///     The training stepper on the node's real DI: real GPU gate, supervisor, ledger, run store, blob store and data directory. Only the script,
///     the runtime's install state and the platform are scripted; no test runs Python.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class ModelLabPlaygroundServiceTests
{
    private const string ForeignToken = "not-this-spawn";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters =
        {
            new JsonStringEnumConverter()
        }
    };

    [Test]
    public async Task ARun_TrainsWithNoGpuHold_SavesItAndItsTrace_AndLeavesNothingBehind()
    {
        var script = new ScriptedPlaygroundJob
        {
            Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        await using var lab = await Lab.StartAsync(script);

        var starting = lab.Service.StartAsync(Config(), CancellationToken.None);
        await script.Started.Task.WaitAsync(TestBudgets.Contended);

        AssertEx.Null(lab.Host.Gate.ExclusiveKind, "A CPU run holds no GPU gate.");
        AssertEx.False(lab.Host.Supervisor.IsKeepWarmSuppressed(), "Nor the runtime-mutation lease.");
        AssertEx.Equal(expected: 0L, lab.Host.Ledger.Reserved.GpuBytes);
#pragma warning disable CA2000 // Given back on the next line.
        AssertEx.Succeeded(lab.Host.Gate.TryBeginExclusive(GpuWorkKind.TrainingRun, out var training));
#pragma warning restore CA2000
        training.Dispose();
        using (var job = JsonDocument.Parse(script.LastJob!))
        {
            AssertEx.Equal("playground", job.RootElement.GetProperty("kind").GetString()!);
            AssertEx.Equal(expected: 1, job.RootElement.GetProperty("contractVersion").GetInt32());
            AssertEx.Equal("xor", job.RootElement.GetProperty("config").GetProperty("dataset").GetString()!);
            AssertEx.Equal(ModelLabPlaygroundSpec.MaxTraceBytes, job.RootElement.GetProperty("maxTraceBytes").GetInt32());
        }

        AssertEx.Equal(lab.Interpreter, script.LastRequest!.ExecutablePath);
        AssertEx.Equal(lab.Service.ScriptPath, script.LastRequest.Arguments[0]);
        script.Hold.SetResult();
        var outcome = await starting.WaitAsync(TestBudgets.Contended);

        var run = AssertEx.NotNull(outcome.Run, outcome.RefusalMessage ?? "saved");
        AssertEx.Equal(ModelLabPlaygroundSpec.TemplateId, run.TemplateId);
        AssertEx.Equal("finished", run.Result.Outcome.Kind);
        AssertEx.Equal((long)Encoding.UTF8.GetByteCount(MinimalTrace), run.TraceByteCount);
        AssertEx.Equal(run.Id, (await lab.Service.ListAsync(CancellationToken.None)).Single().Id);
        var read = await lab.Service.GetAsync(run.Id, CancellationToken.None);
        AssertEx.True(read.Trace.HasValue, read.UnavailableMessage ?? "read");
        AssertEx.Equal(MinimalTrace, Encoding.UTF8.GetString(read.Trace.GetValueOrDefault().Span));
        AssertEx.Empty(Directory.EnumerateFileSystemEntries(lab.WorkRoot), "No work directory survives the run.");
        AssertEx.Equal(expected: 0, lab.PublishedEvents, "A run publishes no event.");
        AssertEx.Empty(lab.Events.Replay(run.Id, afterSequence: 0).Events);
    }

    [Test]
    public async Task EveryRefusalBeforeTheScript_StartsNothing_AndSavesNothing()
    {
        var script = new ScriptedPlaygroundJob();
        await using var lab = await Lab.StartAsync(script);

        lab.Service.PlatformProbe = static () => false;
        await lab.ExpectAsync(ModelLabPlaygroundRefusal.UnsupportedPlatform, "Linux x64 only");
        lab.Service.PlatformProbe = static () => true;

        lab.Host.Runtime.GetStatus().Returns(Status(TrainingRuntimePhase.InstallingPackages, terminal: false));
        await lab.ExpectAsync(ModelLabPlaygroundRefusal.RuntimeUnavailable, ModelLabRuntimeJob.RuntimeChangingMessage);
        lab.Host.Runtime.GetStatus().Returns(Status(TrainingRuntimePhase.Idle, terminal: true));
        await lab.ExpectAsync(ModelLabPlaygroundRefusal.RuntimeUnavailable, "not installed");
        lab.Host.Runtime.GetStatus().Returns(Status(TrainingRuntimePhase.Ready, terminal: true));
        lab.Host.Runtime.ResolveInterpreterPath().Returns((string?)null);
        await lab.ExpectAsync(ModelLabPlaygroundRefusal.RuntimeUnavailable, "not installed");
        lab.Host.Runtime.ResolveInterpreterPath().Returns(lab.Interpreter);
        File.Delete(lab.Service.ScriptPath);
        await lab.ExpectAsync(ModelLabPlaygroundRefusal.RuntimeUnavailable, "not installed");

        AssertEx.Equal(expected: 0, script.SpawnCount);
        AssertEx.False(lab.Host.Services.GetRequiredService<TrainingRuntimeUsers>().InUse, "A refused entry holds no registration.");
        AssertEx.Empty(await lab.Service.ListAsync(CancellationToken.None));
    }

    [Test]
    public async Task ASecondRunWhileOneRuns_IsBusy()
    {
        var script = new ScriptedPlaygroundJob
        {
            Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        await using var lab = await Lab.StartAsync(script);

        var first = lab.Service.StartAsync(Config(), CancellationToken.None);
        await script.Started.Task.WaitAsync(TestBudgets.Contended);
        await lab.ExpectAsync(ModelLabPlaygroundRefusal.Busy, "in progress");
        script.Hold.SetResult();

        AssertEx.NotNull((await first.WaitAsync(TestBudgets.Contended)).Run);
        AssertEx.NotNull((await lab.Service.StartAsync(Config(), CancellationToken.None)).Run, "The flag comes back with the run.");
    }

    /// <summary>Every way a script run or its outputs fail is a <c>RunFailed</c> that saves no row and no blob, publishes nothing and leaves no work directory.</summary>
    [Test]
    public async Task EveryScriptOrOutputFailure_IsRunFailed_AndSavesNothing()
    {
        var script = new ScriptedPlaygroundJob();
        await using var lab = await Lab.StartAsync(script);
        var oversize = $"{{\"pad\":\"{new string('x', ModelLabPlaygroundSpec.MaxTraceBytes)}\"}}";

        await lab.ExpectFailureAsync(script, "the script's own words",
            lines: [Handshake(1), """{"event":"error","category":"internal","message":"the script's own words"}""", Done], exitCode: 1);
        await lab.ExpectFailureAsync(script, "exited with status 2", lines: [Handshake(1), Done], exitCode: 2);
        await lab.ExpectFailureAsync(script, "exited with status 0", lines: [Handshake(1)]);
        await lab.ExpectFailureAsync(script, "exited with status 0", lines: [Done]);
        await lab.ExpectFailureAsync(script, "protocol version", lines: [Handshake(ModelLabTraceSpec.ContractVersion), Done]);
        await lab.ExpectFailureAsync(script, "exited with status 0", lines: [Handshake(1), Stamped(Done, ForeignToken)]);
        await lab.ExpectFailureAsync(script, "stopped", lines: [Handshake(1), Done], exitCode: TrainingRunExecutor.CancelledExitCode);
        await lab.ExpectFailureAsync(script, "without writing its trace", trace: null);
        await lab.ExpectFailureAsync(script, "without writing its summary", result: null);
        await lab.ExpectFailureAsync(script, "larger than its", trace: oversize);
        await lab.ExpectFailureAsync(script, "not valid JSON", trace: MinimalTrace[..(MinimalTrace.Length / 2)]);
        await lab.ExpectFailureAsync(script, "format", trace: MinimalTrace.Replace("\"formatVersion\":1", "\"formatVersion\":2", StringComparison.Ordinal));
        await lab.ExpectFailureAsync(script, "'recorded'", trace: MinimalTrace.Replace("\"recorded\":[]", "\"recorded\":null", StringComparison.Ordinal));
        await lab.ExpectFailureAsync(script, "'data.count'",
            trace: MinimalTrace.Replace("\"data\":{\"count\":4,\"probes\":[0,1,2,3],\"x\":[[0,0],[0,1],[1,0],[1,1]],\"y\":[0,1,1,0]}", "\"data\":{}", StringComparison.Ordinal));
        await lab.ExpectFailureAsync(script, "'modules[0].outputActivation'", trace: MinimalTrace.Replace(",\"outputActivation\":null", "", StringComparison.Ordinal));
        await lab.ExpectFailureAsync(script, "'recorded[0].loss'",
            trace: MinimalTrace.Replace("\"recorded\":[]", "\"recorded\":[{\"step\":0,\"forward\":[],\"loss\":{},\"backward\":null,\"update\":null}]", StringComparison.Ordinal));
        await lab.ExpectFailureAsync(script, "'data.probes'", trace: MinimalTrace.Replace("\"probes\":[0,1,2,3]", "\"probes\":[0,-1,2,3]", StringComparison.Ordinal));
        await lab.ExpectFailureAsync(script, "'occurrences[1].visit'", trace: MinimalTrace.Replace("\"visit\":1", "\"visit\":1.5", StringComparison.Ordinal));
        await lab.ExpectFailureAsync(script, "another template", result: MinimalResult.Replace("tiny-mlp-v1", "other-v1", StringComparison.Ordinal));
        await lab.ExpectFailureAsync(script, "cannot read", result: MinimalResult.Replace("\"xor\"", "\"moons\"", StringComparison.Ordinal));
        await lab.ExpectFailureAsync(script, "'config.seed'", result: MinimalResult.Replace(",\"seed\":0", "", StringComparison.Ordinal));
        await lab.ExpectFailureAsync(script, "'config.residual'", result: MinimalResult.Replace("\"residual\":false", "\"residual\":0", StringComparison.Ordinal));

        AssertEx.Empty(await lab.Service.ListAsync(CancellationToken.None));
        AssertEx.False(Directory.Exists(lab.BlobRoot) && Directory.EnumerateFileSystemEntries(lab.BlobRoot).Any(), "No failed run keeps a trace.");
        AssertEx.Equal(expected: 0, lab.PublishedEvents);
    }

    /// <summary>Only this spawn's frames count: a foreign error, done or mismatched handshake changes nothing.</summary>
    [Test]
    public async Task ForeignTokenFrames_AreIgnored()
    {
        var script = new ScriptedPlaygroundJob
        {
            Lines =
            [
                Handshake(1),
                Stamped($$"""{"event":"handshake","contractVersion":{{ModelLabTraceSpec.ContractVersion}}}""", ForeignToken),
                Stamped("""{"event":"error","category":"internal","message":"someone else's"}""", ForeignToken),
                Stamped("""{"event":"done","cancelled":true}""", ForeignToken),
                Done
            ]
        };
        await using var lab = await Lab.StartAsync(script);

        var outcome = await lab.Service.StartAsync(Config(), CancellationToken.None);

        AssertEx.NotNull(outcome.Run, outcome.RefusalMessage ?? "saved");
        AssertEx.False(script.Killed, "A foreign handshake's version is not this script's.");
    }

    [Test]
    public async Task ACancelledRun_StopsTheScript_SavesNothing_AndPublishesNothing()
    {
        var script = new ScriptedPlaygroundJob
        {
            Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        await using var lab = await Lab.StartAsync(script);
        using var stop = new CancellationTokenSource();

        var starting = lab.Service.StartAsync(Config(), stop.Token);
        await script.Started.Task.WaitAsync(TestBudgets.Contended);
        await stop.CancelAsync();
        var outcome = await starting.WaitAsync(TestBudgets.Contended);

        AssertEx.True(script.StopRequested, "The request's cancellation reaches the script as a stop.");
        AssertEx.Equal(ModelLabPlaygroundRefusal.RunFailed, outcome.Refusal!.Value, outcome.RefusalMessage);
        AssertEx.Contains(outcome.RefusalMessage, "stopped");
        AssertEx.Empty(await lab.Service.ListAsync(CancellationToken.None));
        AssertEx.Empty(Directory.EnumerateFileSystemEntries(lab.WorkRoot));
        AssertEx.Equal(expected: 0, lab.PublishedEvents);
    }

    /// <summary>A run is a registered runtime user while its script runs: an install is refused then, and admitted once the run returned, threw or was stopped.</summary>
    [Test]
    public async Task AnInstall_WhileARunIsHeld_IsRefused_AndAdmittedHoweverTheRunEnds()
    {
        var script = new ScriptedPlaygroundJob
        {
            Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        await using var lab = await Lab.StartAsync(script);
        ModelLabPyTorchSourceServiceTests.AdmitsLikeTheProvider(lab.Host.Runtime);
        var orchestration = lab.Host.Services.GetRequiredService<TrainingRuntimeOrchestrationService>();

        // Returns.
        var starting = lab.Service.StartAsync(Config(), CancellationToken.None);
        await script.Started.Task.WaitAsync(TestBudgets.Contended);
        AssertEx.Equal(TrainingRuntimeInstallOutcome.GpuJobRunning, (await orchestration.InstallAsync(CancellationToken.None)).Outcome);
        AssertEx.Equal(TrainingRuntimeRemoveOutcome.GpuJobRunning, await orchestration.RemoveAsync(CancellationToken.None));
        script.Hold.SetResult();
        AssertEx.NotNull((await starting.WaitAsync(TestBudgets.Contended)).Run);
        AssertEx.Equal(TrainingRuntimeInstallOutcome.Started, (await orchestration.InstallAsync(CancellationToken.None)).Outcome);

        // Throws.
        script.FailsToSpawn = true;
        _ = await AssertEx.ThrowsAsync<InvalidOperationException>(() => lab.Service.StartAsync(Config(), CancellationToken.None));
        AssertEx.Equal(TrainingRuntimeInstallOutcome.Started, (await orchestration.InstallAsync(CancellationToken.None)).Outcome);
        script.FailsToSpawn = false;

        // Stopped.
        script.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var spawned = script.NextStarted();
        using var stop = new CancellationTokenSource();
        starting = lab.Service.StartAsync(Config(), stop.Token);
        await spawned.WaitAsync(TestBudgets.Contended);
        AssertEx.Equal(TrainingRuntimeInstallOutcome.GpuJobRunning, (await orchestration.InstallAsync(CancellationToken.None)).Outcome);
        await stop.CancelAsync();
        AssertEx.Equal(ModelLabPlaygroundRefusal.RunFailed, (await starting.WaitAsync(TestBudgets.Contended)).Refusal!.Value);
        AssertEx.Equal(TrainingRuntimeInstallOutcome.Started, (await orchestration.InstallAsync(CancellationToken.None)).Outcome);
    }

    /// <summary>A row save that throws before the row landed drops the trace scope; nothing else would ever name it.</summary>
    [Test]
    public async Task AFailedRowSave_WithNoRowInAFreshRead_DropsTheTraceScope()
    {
        var script = new ScriptedPlaygroundJob();
        await using var lab = await Lab.StartAsync(script, rowLandsBeforeTheThrow: false);

        _ = await AssertEx.ThrowsAsync<InvalidOperationException>(() => lab.Service.StartAsync(Config(), CancellationToken.None));

        AssertEx.True(Directory.Exists(lab.BlobRoot), "The blob was written before the row.");
        AssertEx.Empty(Directory.EnumerateFileSystemEntries(lab.BlobRoot));
        AssertEx.Empty(await lab.Service.ListAsync(CancellationToken.None));
    }

    /// <summary>A row save that throws after the row landed keeps the trace scope: the row still names it, and stays readable.</summary>
    [Test]
    public async Task AFailedRowSave_WhoseRowLanded_KeepsTheTraceScope()
    {
        var script = new ScriptedPlaygroundJob();
        await using var lab = await Lab.StartAsync(script, rowLandsBeforeTheThrow: true);

        _ = await AssertEx.ThrowsAsync<InvalidOperationException>(() => lab.Service.StartAsync(Config(), CancellationToken.None));

        var run = (await lab.Service.ListAsync(CancellationToken.None)).Single();
        AssertEx.True((await lab.Service.GetAsync(run.Id, CancellationToken.None)).Trace.HasValue, "The kept scope still serves the row's trace.");
    }

    /// <summary>A save trims to the newest runs and drops only the scopes of rows it removed; a scope no row names is left alone, since nothing sweeps.</summary>
    [Test]
    public async Task ASave_TrimsToTheNewestRuns_AndDropsOnlyTheRemovedRowsScopes()
    {
        var script = new ScriptedPlaygroundJob();
        await using var lab = await Lab.StartAsync(script);
        var seeded = await lab.SeedOldRunsAsync(ModelLabPlaygroundSpec.MaxRetainedRuns);
        var stray = Path.Combine(lab.BlobRoot, Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(stray);

        await lab.ExpectFailureAsync(script, "'modules[0].outputActivation'", trace: MinimalTrace.Replace(",\"outputActivation\":null", "", StringComparison.Ordinal));
        await lab.ExpectFailureAsync(script, "'outcome.updatesApplied'", trace: MinimalTrace.Replace("\"updatesApplied\":8", "\"updatesApplied\":-8", StringComparison.Ordinal));
        AssertEx.Equal(ModelLabPlaygroundSpec.MaxRetainedRuns, (await lab.Service.ListAsync(CancellationToken.None)).Count, "A refused trace trims no run.");
        script.Trace = MinimalTrace;
        var saved = AssertEx.NotNull((await lab.Service.StartAsync(Config(), CancellationToken.None)).Run);

        var kept = (await lab.Service.ListAsync(CancellationToken.None)).Select(static run => run.Id).ToList();
        AssertEx.Equal(ModelLabPlaygroundSpec.MaxRetainedRuns, kept.Count);
        AssertEx.Equal(saved.Id, kept[0]);
        AssertEx.False(kept.Contains(seeded[0]), "The oldest run was trimmed.");
        AssertEx.False(Directory.Exists(lab.ScopeOf(seeded[0])), "Its trace scope went with it.");
        AssertEx.True(seeded.Skip(1).All(id => Directory.Exists(lab.ScopeOf(id))), "Every kept run keeps its scope.");
        AssertEx.True(Directory.Exists(stray), "A scope no trimmed row named is not swept.");
    }

    [Test]
    public async Task ADelete_RemovesTheRowAndItsScope_AndAnUnknownRunIsNotFound()
    {
        var script = new ScriptedPlaygroundJob();
        await using var lab = await Lab.StartAsync(script);
        var run = AssertEx.NotNull((await lab.Service.StartAsync(Config(), CancellationToken.None)).Run);

        await lab.Service.DeleteAsync(run.Id, CancellationToken.None);

        AssertEx.False(Directory.Exists(lab.ScopeOf(run.Id)));
        _ = await AssertEx.ThrowsAsync<ModelLabPlaygroundRunNotFoundException>(() => lab.Service.GetAsync(run.Id, CancellationToken.None));
        _ = await AssertEx.ThrowsAsync<ModelLabPlaygroundRunNotFoundException>(() => lab.Service.DeleteAsync(run.Id, CancellationToken.None));
    }

    /// <summary>Each knob is accepted at both of its limits and refused just outside them, so the published bounds are the enforced ones.</summary>
    [Test]
    public async Task OverHttp_EveryKnob_IsAcceptedAtItsLimits_AndRefusedJustOutsideThem()
    {
        var script = new ScriptedPlaygroundJob();
        await using var lab = await Lab.StartAsync(script);
        using var client = lab.Host.Factory.CreateClient();
        JsonObject[] accepted =
        [
            Knobs("learningRate", ModelLabPlaygroundSpec.MinLearningRate), Knobs("learningRate", ModelLabPlaygroundSpec.MaxLearningRate),
            Knobs("depth", ModelLabPlaygroundSpec.MinDepth), Knobs("depth", ModelLabPlaygroundSpec.MaxDepth),
            Knobs("hidden", ModelLabPlaygroundSpec.MinHidden), Knobs("hidden", ModelLabPlaygroundSpec.MaxHidden),
            Knobs("steps", ModelLabPlaygroundSpec.MinSteps), Knobs("steps", ModelLabPlaygroundSpec.MaxSteps),
            Knobs("seed", ModelLabPlaygroundSpec.MinSeed), Knobs("seed", ModelLabPlaygroundSpec.MaxSeed),
            Knobs("dataset", "spirals")
        ];
        JsonObject[] refused =
        [
            Knobs("learningRate", 0.0000999), Knobs("learningRate", 10.000001), Knobs("learningRate", 0),
            Knobs("depth", ModelLabPlaygroundSpec.MinDepth - 1), Knobs("depth", ModelLabPlaygroundSpec.MaxDepth + 1),
            Knobs("hidden", ModelLabPlaygroundSpec.MinHidden - 1), Knobs("hidden", ModelLabPlaygroundSpec.MaxHidden + 1),
            Knobs("steps", ModelLabPlaygroundSpec.MinSteps - 1), Knobs("steps", ModelLabPlaygroundSpec.MaxSteps + 1),
            Knobs("seed", ModelLabPlaygroundSpec.MinSeed - 1), Knobs("seed", (long)ModelLabPlaygroundSpec.MaxSeed + 1),
            Knobs("dataset", "moons"), Knobs("residual", "yes"), Knobs("depth", null), Without("sharedBlock")
        ];

        foreach (var body in accepted)
        {
            using var response = await lab.PostAsync(client, body);
            AssertEx.Equal(HttpStatusCode.OK, response.StatusCode, $"{body.ToJsonString()}: {await response.Content.ReadAsStringAsync()}");
        }

        foreach (var body in refused)
        {
            using var response = await lab.PostAsync(client, body);
            AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode, $"{body.ToJsonString()}: {await response.Content.ReadAsStringAsync()}");
        }

        AssertEx.Equal(accepted.Length, script.SpawnCount, "A refused request starts no script.");
    }

    [Test]
    public async Task OverHttp_TheRoutesAreOperatorOnly_ARunReadsBack_AndEveryUnreadableTraceIsA409TheRowSurvives()
    {
        var script = new ScriptedPlaygroundJob();
        await using var lab = await Lab.StartAsync(script);
        using var client = lab.Host.Factory.CreateClient();
        const string runs = "/api/local/v1/model-lab/playground/runs";

        KeyValuePair<HttpMethod, string>[] routes =
            [new(HttpMethod.Get, runs), new(HttpMethod.Post, runs), new(HttpMethod.Get, $"{runs}/{Guid.NewGuid()}"), new(HttpMethod.Delete, $"{runs}/{Guid.NewGuid()}")];
        foreach (var (method, path) in routes)
        {
            using var request = new HttpRequestMessage(method, path);
            lab.Host.Factory.AddNonOperatorBearerToken(request);
            request.Headers.Add("Origin", "http://localhost");
            AssertEx.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(request)).StatusCode, $"{method} {path}");
        }

        using var started = await lab.PostAsync(client, Knobs("seed", 7));
        AssertEx.Equal(HttpStatusCode.OK, started.StatusCode, await started.Content.ReadAsStringAsync());
        var run = AssertEx.NotNull(await started.Content.ReadFromJsonAsync<ModelLabPlaygroundRunResponse>(JsonOptions));
        using (var job = JsonDocument.Parse(script.LastJob!))
        {
            AssertEx.Equal(expected: 7, job.RootElement.GetProperty("config").GetProperty("seed").GetInt32(), "The request's knobs reach the job.");
        }

        AssertEx.Equal(expected: 0, run.Config.Seed, "The answer shows the config the script reports it ran.");
        AssertEx.Equal(ModelLabPlaygroundDataset.Xor, run.Config.Dataset);
        AssertEx.Equal("finished", run.Outcome.Kind);

        using var list = await lab.SendAsync(client, HttpMethod.Get, runs);
        var listed = AssertEx.NotNull(await list.Content.ReadFromJsonAsync<ModelLabPlaygroundRunListResponse>(JsonOptions));
        AssertEx.True(listed.PlatformSupported);
        AssertEx.Equal(ModelLabPlaygroundSpec.MaxRetainedRuns, listed.MaxRetainedRuns);
        AssertEx.Equal(run.Id, listed.Runs.Single().Id);

        var route = $"{runs}/{run.Id}";
        using (var detail = await lab.SendAsync(client, HttpMethod.Get, route))
        {
            AssertEx.Equal(HttpStatusCode.OK, detail.StatusCode);
            var body = AssertEx.NotNull(await detail.Content.ReadFromJsonAsync<ModelLabPlaygroundRunDetailResponse>(JsonOptions));
            AssertEx.True(JsonNode.DeepEquals(JsonNode.Parse(MinimalTrace), JsonNode.Parse(body.Trace.GetRawText())), "The trace is passed through as written.");
        }

        var blob = Directory.EnumerateFiles(lab.ScopeOf(run.Id)).Single();
        var original = await File.ReadAllBytesAsync(blob);
        await File.WriteAllBytesAsync(blob, [.. original.Take(original.Length - 1), (byte)(original[^1] ^ 0xFF)]);
        await lab.ExpectTraceUnavailableAsync(client, route, "tampered");
        await File.WriteAllBytesAsync(blob, original);
        await lab.SetRowAsync(run.Id, "trace_byte_count = trace_byte_count + 1");
        await lab.ExpectTraceUnavailableAsync(client, route, "size mismatch");
        await lab.SetRowAsync(run.Id, "trace_byte_count = trace_byte_count - 1, trace_sha256 = '" + new string('0', 64) + "'");
        await lab.ExpectTraceUnavailableAsync(client, route, "hash mismatch");
        File.Delete(blob);
        await lab.ExpectTraceUnavailableAsync(client, route, "missing");

        using var deleted = await lab.SendAsync(client, HttpMethod.Delete, route);
        AssertEx.Equal(HttpStatusCode.NoContent, deleted.StatusCode, "An unreadable run stays deletable.");
        using var gone = await lab.SendAsync(client, HttpMethod.Get, route);
        AssertEx.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        using var again = await lab.SendAsync(client, HttpMethod.Delete, route);
        AssertEx.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    private static ModelLabPlaygroundConfigV1 Config() =>
        new()
        {
            Dataset = ModelLabPlaygroundDataset.Xor,
            LearningRate = 0.5,
            Residual = false,
            SharedBlock = true,
            Depth = 2,
            Hidden = 4,
            Steps = 8,
            Seed = 0
        };

    private static JsonObject Knobs(string knob, JsonNode? value)
    {
        var body = JsonNode.Parse(MinimalConfig)!.AsObject();
        body[knob] = value;
        return body;
    }

    private static JsonObject Without(string knob)
    {
        var body = JsonNode.Parse(MinimalConfig)!.AsObject();
        _ = body.Remove(knob);
        return body;
    }

    private static string Handshake(int contractVersion) =>
        $$"""{"event":"handshake","contractVersion":{{contractVersion}}}""";

    private const string Done = """{"event":"done"}""";

    private static string Stamped(string frame, string token) =>
        string.Concat(frame.AsSpan(0, frame.Length - 1), ",\"token\":\"", token, "\"}");

    private static TrainingRuntimeStatus Status(TrainingRuntimePhase phase, bool terminal) =>
        new()
        {
            Phase = phase,
            IsRunning = !terminal,
            Terminal = terminal,
            LogLines = [],
            LogStartSequence = 0,
            SanitizedError = null,
            Installed = null,
            StartedAtUtc = null,
            CompletedAtUtc = null
        };

    /// <summary>The lab host with this feature's script, a stand-in script file and a counter on the event buffer.</summary>
    private sealed class Lab : IAsyncDisposable
    {
        private readonly TempDirectory _scripts;
        private readonly ModelLabTestSupport.ScriptedProcess _unused;
        private int _published;

        private Lab(ModelLabTestSupport.LabHost host, ModelLabTestSupport.ScriptedProcess unused, TempDirectory scripts)
        {
            Host = host;
            _unused = unused;
            _scripts = scripts;
            Events.EventPublished += (_, _) => Interlocked.Increment(ref _published);
        }

        public ModelLabTestSupport.LabHost Host { get; }

        public ModelLabPlaygroundService Service => Host.Services.GetRequiredService<ModelLabPlaygroundService>();

        public ITrainingRunEventBuffer Events => Host.Services.GetRequiredService<ITrainingRunEventBuffer>();

        public int PublishedEvents => Volatile.Read(ref _published);

        public string Interpreter => Host.Runtime.ResolveInterpreterPath()!;

        public string WorkRoot
        {
            get
            {
                var root = ModelLabScriptRunner.WorkRoot(Host.Services.GetRequiredService<INodeDataDirectory>());
                _ = Directory.CreateDirectory(root);
                return root;
            }
        }

        public string BlobRoot => Path.Combine(Host.DataRoot, "model-lab", "playground");

        /// <param name="rowLandsBeforeTheThrow">Set: the run store's create throws, after saving the row when true, before it when false.</param>
        public static async Task<Lab> StartAsync(ScriptedPlaygroundJob script, bool? rowLandsBeforeTheThrow = null)
        {
#pragma warning disable CA2000 // The lab owns them.
            var unused = new ModelLabTestSupport.ScriptedProcess();
            var scripts = new TempDirectory("playground-script");
#pragma warning restore CA2000
            var host = await ModelLabTestSupport.LabHost.StartAsync(unused, configure: services =>
            {
                services.RemoveAll<ITrainingProcessSpawner>();
                services.AddSingleton<ITrainingProcessSpawner>(script);
                if (rowLandsBeforeTheThrow is { } landed)
                {
                    services.RemoveAll<IModelLabPlaygroundRunStore>();
                    services.AddScoped(provider => ThrowingCreate(ActivatorUtilities.CreateInstance<ModelLabPlaygroundRunStore>(provider), landed));
                }
            });
            var lab = new Lab(host, unused, scripts);
            var scriptPath = scripts.FilePath(TrainingScripts.PlaygroundScriptName);
            await File.WriteAllTextAsync(scriptPath, "# stand-in");
            lab.Service.PlatformProbe = static () => true;
            lab.Service.ScriptPath = scriptPath;
            return lab;
        }

        public string ScopeOf(Guid runId) =>
            Path.Combine(BlobRoot, runId.ToString("N"));

        public async Task ExpectAsync(ModelLabPlaygroundRefusal expected, string messagePart)
        {
            var outcome = await Service.StartAsync(Config(), CancellationToken.None);
            AssertEx.Null(outcome.Run, $"Expected {expected}.");
            AssertEx.Equal(expected, outcome.Refusal!.Value, outcome.RefusalMessage);
            AssertEx.Contains(outcome.RefusalMessage, messagePart);
        }

        /// <summary>One failed run: the script says <paramref name="lines" /> and writes the given outputs (null leaves one unwritten).</summary>
        public async Task ExpectFailureAsync(ScriptedPlaygroundJob script,
            string messagePart,
            string[]? lines = null,
            int exitCode = 0,
            string? trace = MinimalTrace,
            string? result = MinimalResult)
        {
            script.Lines = lines ?? [Handshake(1), Done];
            script.ExitCode = exitCode;
            script.Trace = trace;
            script.Result = result;
            await ExpectAsync(ModelLabPlaygroundRefusal.RunFailed, messagePart);
            AssertEx.Empty(Directory.EnumerateFileSystemEntries(WorkRoot), $"{messagePart}: no work directory survives.");
        }

        public Task<HttpResponseMessage> PostAsync(HttpClient client, JsonObject body) =>
            SendAsync(client, HttpMethod.Post, "/api/local/v1/model-lab/playground/runs", body);

        public async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string route, JsonObject? body = null)
        {
            using var request = Host.Authorized(method, route);
            if (body is not null)
            {
                request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            }

            return await client.SendAsync(request);
        }

        public async Task ExpectTraceUnavailableAsync(HttpClient client, string route, string because)
        {
            using var response = await SendAsync(client, HttpMethod.Get, route);
            AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode, because);
            var blocked = AssertEx.NotNull(await response.Content.ReadFromJsonAsync<ModelLabPlaygroundBlockedResponse>(JsonOptions));
            AssertEx.Equal(ModelLabPlaygroundRefusal.TraceUnavailable, blocked.Reason, because);
            AssertEx.NotEmpty(blocked.Message);
        }

        public async Task SetRowAsync(Guid runId, string assignments)
        {
            await using var scope = Host.Services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
#pragma warning disable EF1002 // The assignments are this test's own constants; the id is a parameter.
            AssertEx.Equal(expected: 1, await context.Database.ExecuteSqlRawAsync($"UPDATE model_lab_playground_runs SET {assignments} WHERE id = {{0}}", runId.ToString().ToUpperInvariant()));
#pragma warning restore EF1002
        }

        /// <summary>Saves <paramref name="count" /> runs dated long before now, each with a trace scope, oldest first.</summary>
        public async Task<IReadOnlyList<Guid>> SeedOldRunsAsync(int count)
        {
            var ids = new List<Guid>();
            await using var scope = Host.Services.CreateAsyncScope();
            var clock = new SteppingTimeProvider();
            var store = new ModelLabPlaygroundRunStore(scope.ServiceProvider.GetRequiredService<NodeChatDbContext>(), clock);
            for (var index = 0; index < count; index++)
            {
                var id = Guid.NewGuid();
                _ = Directory.CreateDirectory(ScopeOf(id));
                _ = await store.CreateAsync(new ModelLabPlaygroundRunCreate
                {
                    Id = id,
                    TemplateId = ModelLabPlaygroundSpec.TemplateId,
                    TraceBlobId = Guid.NewGuid(),
                    TraceSha256 = new string('0', 64),
                    TraceByteCount = 1,
                    ResultJson = Encoding.UTF8.GetBytes(MinimalResult)
                });
                ids.Add(id);
            }

            return ids;
        }

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            _unused.Dispose();
            _scripts.Dispose();
        }

        private static IModelLabPlaygroundRunStore ThrowingCreate(IModelLabPlaygroundRunStore real, bool rowLands)
        {
            var store = Substitute.For<IModelLabPlaygroundRunStore>();
            store.CreateAsync(Arg.Any<ModelLabPlaygroundRunCreate>(), Arg.Any<CancellationToken>())
                 .Returns(call => CreateThenThrowAsync(real, call.Arg<ModelLabPlaygroundRunCreate>(), rowLands));
            store.GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(call => real.GetAsync(call.Arg<Guid>(), call.Arg<CancellationToken>()));
            store.ListAsync(Arg.Any<CancellationToken>()).Returns(call => real.ListAsync(call.Arg<CancellationToken>()));
            return store;
        }

        private static async Task<ModelLabPlaygroundRunRecord> CreateThenThrowAsync(IModelLabPlaygroundRunStore real, ModelLabPlaygroundRunCreate command, bool rowLands)
        {
            if (rowLands)
            {
                _ = await real.CreateAsync(command, CancellationToken.None);
            }

            throw new InvalidOperationException("The scripted row save failed.");
        }
    }

    /// <summary>A scripted <c>nnplayground.py</c>: it records each job, writes the outputs it is given and says its lines with the spawn's token.</summary>
    /// <remarks>Held, it waits for the test or for a stop; a stopped run says nothing more and exits with the trainer's cancelled status.</remarks>
    internal sealed class ScriptedPlaygroundJob : ITrainingProcessSpawner
    {
        private TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _spawns;

        public string[] Lines { get; set; } = [Handshake(1), """{"event":"phase","phase":"loading"}""", """{"event":"phase","phase":"training"}""", """{"event":"phase","phase":"saving"}""", Done];

        public int ExitCode { get; set; }

        /// <summary>Null leaves the trace unwritten.</summary>
        public string? Trace { get; set; } = MinimalTrace;

        public string? Result { get; set; } = MinimalResult;

        public TaskCompletionSource? Hold { get; set; }

        public bool FailsToSpawn { get; set; }

        public TaskCompletionSource Started => _started;

        public int SpawnCount => Volatile.Read(ref _spawns);

        public string? LastJob { get; private set; }

        public TrainingSpawnRequest? LastRequest { get; private set; }

        public volatile bool StopRequested;

        public volatile bool Killed;

        /// <summary>A fresh signal for the next spawn.</summary>
        public Task NextStarted()
        {
            _started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _started.Task;
        }

        public ITrainingProcessHandle Spawn(TrainingSpawnRequest request)
        {
            if (FailsToSpawn)
            {
                throw new InvalidOperationException("The scripted spawn failed.");
            }

            _ = Interlocked.Increment(ref _spawns);
            LastRequest = request;
            LastJob = File.ReadAllText(request.Arguments[2]);
            using var job = JsonDocument.Parse(LastJob);
            var handle = new Handle(this, job.RootElement.GetProperty("tracePath").GetString()!, job.RootElement.GetProperty("resultPath").GetString()!, request.RunToken);
            _started.TrySetResult();
            return handle;
        }

        private sealed class Handle : ITrainingProcessHandle
        {
            private readonly ScriptedPlaygroundJob _owner;
            private readonly string _resultPath;
            private readonly string _runToken;
            private readonly TaskCompletionSource _stop = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly string _tracePath;
            private volatile bool _killed;

            public Handle(ScriptedPlaygroundJob owner, string tracePath, string resultPath, string runToken)
            {
                _owner = owner;
                _tracePath = tracePath;
                _resultPath = resultPath;
                _runToken = runToken;
            }

            public TrainingLaunchReceipt Receipt { get; } = new()
            {
                Pid = 4444,
                Pgid = 4444,
                ExecutablePath = "/venv/bin/python",
                StartTicks = 1,
                RunToken = "token"
            };

            public async IAsyncEnumerable<string> ReadOutputAsync([EnumeratorCancellation] CancellationToken cancellationToken)
            {
                if (_owner.Hold is { } hold)
                {
                    _ = await Task.WhenAny(hold.Task, _stop.Task).WaitAsync(TestBudgets.Contended, cancellationToken);
                }

                if (_stop.Task.IsCompleted)
                {
                    yield break;
                }

                if (_owner.Trace is { } trace)
                {
                    await File.WriteAllTextAsync(_tracePath, trace, cancellationToken);
                }

                if (_owner.Result is { } result)
                {
                    await File.WriteAllTextAsync(_resultPath, result, cancellationToken);
                }

                foreach (var line in _owner.Lines)
                {
                    yield return line.Contains("\"token\"", StringComparison.Ordinal) ? line : Stamped(line, _runToken);
                }
            }

            /// <summary>A kill's signal status, the trainer's cancelled status after a stop, else the scripted one.</summary>
            public Task<int> WaitForExitAsync(CancellationToken cancellationToken)
            {
                if (_killed)
                {
                    return Task.FromResult(137);
                }

                return Task.FromResult(_stop.Task.IsCompleted ? TrainingRunExecutor.CancelledExitCode : _owner.ExitCode);
            }

            public void RequestStop()
            {
                _owner.StopRequested = true;
                _stop.TrySetResult();
            }

            public void KillGroup()
            {
                _killed = true;
                _owner.Killed = true;
            }

            public Task WriteLineAsync(string line, CancellationToken cancellationToken) =>
                Task.CompletedTask;

            public void Dispose()
            {
            }
        }
    }
}
