namespace XE_Local_AI_Engine.Tests.ModelLab;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.ModelLab;
using XE_Local_AI_Engine.Client.Services.Training.Evaluation;
using XE_Local_AI_Engine.Client.Services.Training.Runs;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Tests.Testing;
using static ModelLabExperimentServiceTests;

/// <summary>
///     A steering job on the node's real DI: real gate, lease, ledger, store and blobs. The script, the harness and the arm servers are scripted.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class ModelLabSteeringJobTests
{
    private const string VectorPlaintext = "the-steering-vector-bytes";

    [Test]
    public async Task AJob_DerivesThenRunsTheArmsInOrder_HoldingTheGateAndLeaseThroughout_WithoutTheReservation()
    {
        using var process = new ScriptedProcess();
        var harness = new ScriptedHarness();
        await using var host = await StartHostAsync(process, harness);
        var checks = new List<string>();
        harness.BeforeBody = arm =>
        {
            checks.Add($"{arm}:{host.Gate.ExclusiveKind}:{host.Supervisor.IsKeepWarmSuppressed()}:{host.Ledger.Reserved.GpuBytes}");
            return Task.CompletedTask;
        };

        var start = await host.Service.StartExperimentAsync(Request(host), CancellationToken.None);
        var id = start.ExperimentId!.Value;
        await process.Spawned.WaitAsync(TestBudgets.Contended);
        using (var job = JsonDocument.Parse(process.ReadJobFile()))
        {
            AssertEx.Equal("steering", job.RootElement.GetProperty("kind").GetString());
            AssertEx.Equal(expected: 15, job.RootElement.GetProperty("layer").GetInt32(), "The full-attention block nearest 0.6 of 24.");
            AssertEx.Equal("kind good", job.RootElement.GetProperty("positivePrompts")[0].GetString());
        }

        AssertEx.True(host.Ledger.Reserved.GpuBytes > 0, "The derive holds the reservation.");
        process.Finish(CompleteRun, exitCode: 0);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);

        var gate = nameof(GpuWorkKind.ModelLabExperiment);
        AssertEx.Equal($"0:{gate}:True:0|1:{gate}:True:0|2:{gate}:True:0", string.Join('|', checks), "Gate and lease held in every arm, reservation given back before the first.");
        AssertEx.Null(host.Gate.ExclusiveKind);
        AssertEx.False(host.Supervisor.IsKeepWarmSuppressed(), "The lease comes back after the last arm.");
        AssertEx.Equal(expected: 0L, host.Ledger.Reserved.GpuBytes, "The reservation is released once.");
        var record = AssertEx.NotNull(await host.Service.GetAsync(id, CancellationToken.None));
        AssertEx.Equal(ModelLabExperimentKind.Steering, record.Kind);
        AssertEx.Equal(ModelLabExperimentStatus.Succeeded, record.Status);
        var result = ReadResult(record.ResultJson);
        var steering = result.Steering!;
        AssertEx.Equal(expected: 15, steering.Layer);
        AssertEx.Equal("F", steering.LayerKind);
        AssertEx.Equal(expected: 2.0, steering.Scale);
        AssertEx.Equal("kind good", steering.PositivePrompts[0]);
        AssertEx.Equal("baseline,vector,random", string.Join(',', steering.Arms.Select(static arm => arm.Name)));
        AssertEx.Equal("first prompt|second prompt", string.Join('|', steering.Arms[1].Outputs.Select(static output => output.Prompt)));
        AssertEx.Equal("arm 1", steering.Arms[1].Outputs[0].Text);
        AssertEx.Equal("7,8,9", string.Join(',', steering.Arms[1].Outputs[0].TokenIds));
        AssertEx.Null(steering.Arms[0].VectorSha256);
        AssertEx.Equal(steering.VectorSha256!, steering.Arms[1].VectorSha256!);
        AssertEx.Equal(steering.RandomVectorSha256!, steering.Arms[2].VectorSha256!);
        AssertEx.NotEqual(steering.VectorSha256!, steering.RandomVectorSha256!);
        AssertEx.Equal(expected: 2.0, steering.Arms[2].Scale!.Value);
        AssertEx.Equal("Cuda", steering.Arms[0].Runtime!);
        AssertEx.Equal("b1", steering.Arms[0].Build!);
        AssertEx.False(Directory.Exists(host.WorkDirectory(id)), "Neither the vectors nor the prompts stay in plaintext.");
    }

    [Test]
    public async Task EachArmRequest_IsGreedyOnOneSeed_WithoutThePromptCache_AndWithoutThinking()
    {
        using var process = new ScriptedProcess();
        var harness = new ScriptedHarness();
        await using var host = await StartHostAsync(process, harness);
        _ = await host.Service.StartExperimentAsync(Request(host), CancellationToken.None);
        process.Finish(CompleteRun, exitCode: 0);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);

        AssertEx.Equal(expected: 6, harness.ArmServer.Requests.Count, "Two prompts on each of three arms.");
        var bodies = harness.ArmServer.Requests.Select(static request => JsonDocument.Parse(request).RootElement).ToArray();
        AssertEx.True(bodies.All(static body => body.GetProperty("temperature").GetInt32() == 0 && !body.GetProperty("cache_prompt").GetBoolean()));
        AssertEx.Equal(expected: 1, bodies.Select(static body => body.GetProperty("seed").GetInt32()).Distinct().Count());
        AssertEx.True(bodies.All(static body => !body.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean()));
    }

    [Test]
    public async Task TheVector_IsEncryptedOnDisk_AndTheTraceRouteDoesNotServeIt()
    {
        using var process = new ScriptedProcess();
        await using var host = await StartHostAsync(process, new ScriptedHarness());
        var start = await host.Service.StartExperimentAsync(Request(host), CancellationToken.None);
        process.Finish(CompleteRun, exitCode: 0);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);
        var id = start.ExperimentId!.Value;

        var blob = Directory.GetFiles(Path.Combine(host.DataRoot, "model-lab", "artifacts", id.ToString("N"))).Single();
        AssertEx.False(Encoding.UTF8.GetString(await File.ReadAllBytesAsync(blob)).Contains(VectorPlaintext, StringComparison.Ordinal), "The vector at rest is ciphertext.");
        AssertEx.Equal(ModelLabTraceReadStatus.NotFound, (await host.Service.ReadTraceAsync(id, CancellationToken.None)).Status);

        using var client = host.Factory.CreateClient();
        using var read = host.Authorized(HttpMethod.Get, $"/api/local/v1/model-lab/experiments/{id}");
        using var response = await client.SendAsync(read);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        AssertEx.Equal("Steering", body.RootElement.GetProperty("kind").GetString());
        AssertEx.Equal(JsonValueKind.Null, body.RootElement.GetProperty("traceByteCount").ValueKind, "A page must not take the vector's size for a trace's.");
        AssertEx.Equal(expected: 3, body.RootElement.GetProperty("result").GetProperty("steering").GetProperty("arms").GetArrayLength());

        using var trace = host.Authorized(HttpMethod.Get, $"/api/local/v1/model-lab/experiments/{id}/trace");
        using var traceResponse = await client.SendAsync(trace);
        AssertEx.Equal(HttpStatusCode.NotFound, traceResponse.StatusCode);
    }

    [Test]
    public async Task ACancelBetweenArms_StopsTheRest_AndKeepsTheFinishedArm()
    {
        using var process = new ScriptedProcess();
        var harness = new ScriptedHarness();
        await using var host = await StartHostAsync(process, harness);
        var start = await host.Service.StartExperimentAsync(Request(host), CancellationToken.None);
        var id = start.ExperimentId!.Value;
        harness.AfterBody = arm =>
        {
            AssertEx.True(arm > 0 || host.Service.Cancel(id));
            return Task.CompletedTask;
        };

        process.Finish(CompleteRun, exitCode: 0);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);

        var record = AssertEx.NotNull(await host.Service.GetAsync(id, CancellationToken.None));
        AssertEx.Equal(ModelLabExperimentStatus.Cancelled, record.Status);
        AssertEx.Equal("baseline", ReadResult(record.ResultJson).Steering!.Arms.Single().Name);
        AssertEx.Equal(expected: 1, harness.BodiesRun);
        AssertEx.Null(host.Gate.ExclusiveKind);
        AssertEx.False(Directory.Exists(host.WorkDirectory(id)));
    }

    [Test]
    public async Task AFailedArm_KeepsTheEarlierOnes_AndSaysWhy()
    {
        using var process = new ScriptedProcess();
        var harness = new ScriptedHarness();
        harness.ArmServer.FailOnRequest = 3;
        await using var host = await StartHostAsync(process, harness);
        var start = await host.Service.StartExperimentAsync(Request(host), CancellationToken.None);
        process.Finish(CompleteRun, exitCode: 0);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);

        var record = AssertEx.NotNull(await host.Service.GetAsync(start.ExperimentId!.Value, CancellationToken.None));
        AssertEx.Equal(ModelLabExperimentStatus.Failed, record.Status);
        AssertEx.Contains(record.ErrorMessage!, "HTTP 500", StringComparison.Ordinal);
        AssertEx.False(record.ErrorMessage!.Contains("Exception", StringComparison.Ordinal), record.ErrorMessage);
        AssertEx.Equal("baseline", ReadResult(record.ResultJson).Steering!.Arms.Single().Name);
        AssertEx.Null(host.Gate.ExclusiveKind);
        AssertEx.False(host.Supervisor.IsKeepWarmSuppressed());
    }

    [Test]
    public async Task ALastServerNotSeenToExit_FailsTheRow_AndKeepsAllThreeArms()
    {
        using var process = new ScriptedProcess();
        var harness = new ScriptedHarness
        {
            LastExitObserved = false
        };
        await using var host = await StartHostAsync(process, harness);
        var start = await host.Service.StartExperimentAsync(Request(host), CancellationToken.None);
        process.Finish(CompleteRun, exitCode: 0);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);

        var record = AssertEx.NotNull(await host.Service.GetAsync(start.ExperimentId!.Value, CancellationToken.None));
        AssertEx.Equal(ModelLabExperimentStatus.Failed, record.Status);
        AssertEx.Contains(record.ErrorMessage!, "not seen to exit", StringComparison.Ordinal);
        AssertEx.False(record.ErrorMessage!.Contains("restart", StringComparison.OrdinalIgnoreCase), "Its spawn receipt is gone by now, so no restart would find it.");
        AssertEx.Equal(expected: 3, ReadResult(record.ResultJson).Steering!.Arms.Count);
    }

    [Test]
    public async Task AFailedDerive_StartsNoArm_AndGivesEverythingBack()
    {
        using var process = new ScriptedProcess();
        var harness = new ScriptedHarness();
        await using var host = await StartHostAsync(process, harness);
        var start = await host.Service.StartExperimentAsync(Request(host), CancellationToken.None);
        process.Finish([Handshake, """{"event":"error","category":"trace","message":"ValueError: no usable length"}"""], exitCode: 1);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);

        var record = AssertEx.NotNull(await host.Service.GetAsync(start.ExperimentId!.Value, CancellationToken.None));
        AssertEx.Equal(ModelLabExperimentStatus.Failed, record.Status);
        AssertEx.Contains(record.ErrorMessage!, "no usable length", StringComparison.Ordinal);
        AssertEx.Equal(expected: 0, harness.BodiesRun);
        AssertEx.Equal(expected: 0L, host.Ledger.Reserved.GpuBytes);
        AssertEx.Null(host.Gate.ExclusiveKind);
    }

    /// <summary>A vector there is dropped by llama-server with no error, so it would cost a derive and three loads to produce a second baseline.</summary>
    [Test]
    [Arguments(0)]
    [Arguments(24)]
    [Arguments(99)]
    [Arguments(-1)]
    public async Task ALayerNoVectorCanReach_IsRefusedBeforeTheGate(int layer)
    {
        using var process = new ScriptedProcess();
        await using var host = await StartHostAsync(process, new ScriptedHarness());

        var start = await host.Service.StartExperimentAsync(Request(host, layer), CancellationToken.None);

        AssertEx.Equal(ModelLabStartRefusal.InvalidLayer, start.Refusal!.Value);
        AssertEx.Contains(start.RefusalMessage!, "layers 1 to 23", StringComparison.Ordinal);
        AssertEx.Null(process.Request);
        AssertEx.Null(host.Gate.ExclusiveKind);
        AssertEx.Equal(expected: 0, (await host.Service.ListAsync(CancellationToken.None)).Count);
    }

    [Test]
    public async Task TheCallersLayer_IsUsed_WhenItIsValid_EvenALinearOne()
    {
        using var process = new ScriptedProcess();
        await using var host = await StartHostAsync(process, new ScriptedHarness());

        var start = await host.Service.StartExperimentAsync(Request(host, layer: 12), CancellationToken.None);

        AssertEx.True(start.ExperimentId.HasValue);
        await process.Spawned.WaitAsync(TestBudgets.Contended);
        using (var job = JsonDocument.Parse(process.ReadJobFile()))
        {
            AssertEx.Equal(expected: 12, job.RootElement.GetProperty("layer").GetInt32());
        }

        process.Finish(CompleteRun, exitCode: 0);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);
    }

    [Test]
    public async Task TheStartRoute_RefusesWhatTheValidatorBounds()
    {
        using var process = new ScriptedProcess();
        await using var host = await StartHostAsync(process, new ScriptedHarness());
        using var client = host.Factory.CreateClient();

        foreach (var steering in new object[]
                 {
                     new
                     {
                         positivePrompts = Array.Empty<string>(),
                         negativePrompts = new[]
                         {
                             "b"
                         }
                     },
                     new
                     {
                         positivePrompts = new[]
                         {
                             "a"
                         },
                         negativePrompts = new[]
                         {
                             "   "
                         }
                     },
                     new
                     {
                         positivePrompts = new[]
                         {
                             new string('a', ModelLabTraceSpec.MaxContrastPromptCharacters + 1)
                         },
                         negativePrompts = new[]
                         {
                             "b"
                         }
                     },
                     new
                     {
                         positivePrompts = new[]
                         {
                             "a"
                         },
                         negativePrompts = new[]
                         {
                             "b"
                         },
                         scale = 0.0
                     },
                     new
                     {
                         positivePrompts = new[]
                         {
                             "a"
                         },
                         negativePrompts = new[]
                         {
                             "b"
                         },
                         scale = 21.0
                     },
                     new
                     {
                         positivePrompts = new[]
                         {
                             "a"
                         },
                         negativePrompts = new[]
                         {
                             "b"
                         },
                         additionalTestPrompts = Enumerable.Repeat("t", ModelLabTraceSpec.MaxAdditionalTestPrompts + 1).ToArray()
                     }
                 })
        {
            using var request = host.Authorized(HttpMethod.Post, "/api/local/v1/model-lab/experiments");
            request.Content = JsonContent.Create(new
            {
                baseArtifactId = host.Request().BaseArtifactId,
                installedModelName = Gguf,
                prompt = "first prompt",
                licenseConfirmed = true,
                steering
            });
            using var response = await client.SendAsync(request);
            AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode, JsonSerializer.Serialize(steering));
            // The shape the page's error message reads a field's own reason from.
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            AssertEx.False(string.IsNullOrWhiteSpace(problem.RootElement.GetProperty("errors")[0].GetProperty("reason").GetString()));
        }

        AssertEx.Null(process.Request);
    }

    /// <summary>An arm's server is on loopback: a proxy from the environment must not carry its requests, and it has no reason to redirect one.</summary>
    [Test]
    public async Task TheRegisteredArmClient_UsesNoProxy_AndFollowsNoRedirect()
    {
        using var process = new ScriptedProcess();
        await using var host = await StartHostAsync(process, new ScriptedHarness());

        var handler = host.Services.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(ModelLabArmRunner.HttpClientName);
        while (handler is DelegatingHandler delegating)
        {
            handler = delegating.InnerHandler!;
        }

        var primary = AssertEx.NotNull(handler as SocketsHttpHandler, "The primary handler is the one the registration names.");
        AssertEx.False(primary.UseProxy);
        AssertEx.False(primary.AllowAutoRedirect);
    }

    [Test]
    public async Task TheReadLease_StaysHeldThroughTheLastArm_AndGoesAfterIt()
    {
        var lease = Lease();
        var bodyChecks = new List<int>();
        using var process = new ScriptedProcess();
        var harness = new ScriptedHarness();
        await using var host = await StartHostAsync(process, harness, lease);
        harness.AfterBody = async arm =>
        {
            await lease.DidNotReceive().DisposeAsync();
            bodyChecks.Add(arm);
        };

        _ = await host.Service.StartExperimentAsync(Request(host), CancellationToken.None);
        process.Finish(CompleteRun, exitCode: 0);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);

        AssertEx.Equal("0,1,2", string.Join(',', bodyChecks));
        await lease.Received(1).DisposeAsync();
    }

    /// <summary>The save is the store's failure, not the server's, and what finished is still in memory to be recorded with the row's end.</summary>
    [Test]
    public async Task AFailedSave_KeepsTheFinishedArms_AndBlamesTheSave()
    {
        using var process = new ScriptedProcess();
        var saves = 0;
        await using var host = await StartHostAsync(process, new ScriptedHarness(), configure: services => WrapStore(services, store => new FaultingStore(store)
        {
            BeforeProgress = () =>
            {
                // The derive, then the baseline; the vector arm's save fails.
                if (Interlocked.Increment(ref saves) == 3)
                {
                    throw new InvalidOperationException("database is locked");
                }
            }
        }));

        var start = await host.Service.StartExperimentAsync(Request(host), CancellationToken.None);
        process.Finish(CompleteRun, exitCode: 0);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);

        var record = AssertEx.NotNull(await host.Service.GetAsync(start.ExperimentId!.Value, CancellationToken.None));
        AssertEx.Equal(ModelLabExperimentStatus.Failed, record.Status);
        AssertEx.Contains(record.ErrorMessage!, "could not be saved", StringComparison.Ordinal);
        AssertEx.False(record.ErrorMessage!.Contains("server", StringComparison.OrdinalIgnoreCase), record.ErrorMessage);
        AssertEx.Equal("baseline,vector", string.Join(',', ReadResult(record.ResultJson).Steering!.Arms.Select(static arm => arm.Name)));
    }

    /// <summary>The first save carries the vector's reference, so losing it would leave a row naming a hash with no blob, and a blob nothing refers to.</summary>
    [Test]
    public async Task AFailedFirstSave_StillRecordsTheVectorsBlob()
    {
        using var process = new ScriptedProcess();
        var harness = new ScriptedHarness();
        await using var host = await StartHostAsync(process, harness, configure: services => WrapStore(services, static store => new FaultingStore(store)
        {
            BeforeProgress = static () => throw new InvalidOperationException("database is locked")
        }));

        var start = await host.Service.StartExperimentAsync(Request(host), CancellationToken.None);
        process.Finish(CompleteRun, exitCode: 0);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);

        var id = start.ExperimentId!.Value;
        var record = AssertEx.NotNull(await host.Service.GetAsync(id, CancellationToken.None));
        AssertEx.Equal(ModelLabExperimentStatus.Failed, record.Status);
        AssertEx.Contains(record.ErrorMessage!, "could not be saved", StringComparison.Ordinal);
        AssertEx.Equal(expected: 0, harness.BodiesRun);
        AssertEx.NotNull(ReadResult(record.ResultJson).Steering!.VectorSha256);
        var blob = Directory.GetFiles(Path.Combine(host.DataRoot, "model-lab", "artifacts", id.ToString("N"))).Single();
        AssertEx.Equal(record.TraceBlobId!.Value.ToString("N"), Path.GetFileNameWithoutExtension(blob));
        AssertEx.True(record.TraceByteCount > 0, "The row's size is the blob's.");
        AssertEx.NotNull(record.TraceSha256);
    }

    [Test]
    public async Task AFailedDerive_LeavesTheResultColumnNull()
    {
        using var process = new ScriptedProcess();
        await using var host = await StartHostAsync(process, new ScriptedHarness());
        var start = await host.Service.StartExperimentAsync(Request(host), CancellationToken.None);
        process.Finish([Handshake], exitCode: 1);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);

        AssertEx.False((await host.Service.GetAsync(start.ExperimentId!.Value, CancellationToken.None))!.ResultJson.HasValue);
    }

    /// <summary>A throw outside the arms ends in the service's own last resort, which must not lose what the row had.</summary>
    [Test]
    public async Task AFailureThatEscapesTheArms_StillKeepsTheFinishedArms()
    {
        using var process = new ScriptedProcess();
        var harness = new ScriptedHarness();
        await using var host = await StartHostAsync(process, harness);
        var start = await host.Service.StartExperimentAsync(Request(host), CancellationToken.None);
        harness.AfterBody = arm => arm == 1 ? Task.FromException(new InvalidCastException("a bug")) : Task.CompletedTask;

        process.Finish(CompleteRun, exitCode: 0);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);

        var record = AssertEx.NotNull(await host.Service.GetAsync(start.ExperimentId!.Value, CancellationToken.None));
        AssertEx.Equal(ModelLabExperimentStatus.Failed, record.Status);
        AssertEx.Contains(record.ErrorMessage!, "a bug", StringComparison.Ordinal);
        AssertEx.Equal("baseline,vector", string.Join(',', ReadResult(record.ResultJson).Steering!.Arms.Select(static arm => arm.Name)));
    }

    [Test]
    public async Task ACancelWhileTheGgufIsVerified_IsCancelled_AndNothingSpawns()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var leases = Substitute.For<ITrainingEvaluationInstalledModelLeaseProvider>();
        leases.AcquireAsync(Gguf, Arg.Any<CancellationToken>()).Returns(async call =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>());
            return Lease();
        });
        using var process = new ScriptedProcess();
        await using var host = await StartHostAsync(process, new ScriptedHarness(), leases: leases);

        var start = await host.Service.StartExperimentAsync(Request(host), CancellationToken.None);
        var id = start.ExperimentId!.Value;
        await entered.Task.WaitAsync(TestBudgets.Contended);
        AssertEx.True(host.Service.Cancel(id));
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);

        var record = AssertEx.NotNull(await host.Service.GetAsync(id, CancellationToken.None));
        AssertEx.Equal(ModelLabExperimentStatus.Cancelled, record.Status);
        AssertEx.Null(process.Request);
        AssertEx.Null(host.Gate.ExclusiveKind);
    }

    [Test]
    public async Task AGgufThatCannotBeRead_FailsWithTheReason_AndNoTypeName()
    {
        var leases = Substitute.For<ITrainingEvaluationInstalledModelLeaseProvider>();
        leases.AcquireAsync(Gguf, Arg.Any<CancellationToken>()).Returns<Task<ITrainingEvaluationInstalledModelLease>>(_ => throw new IOException("the file went away"));
        using var process = new ScriptedProcess();
        await using var host = await StartHostAsync(process, new ScriptedHarness(), leases: leases);

        var start = await host.Service.StartExperimentAsync(Request(host), CancellationToken.None);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);

        var record = AssertEx.NotNull(await host.Service.GetAsync(start.ExperimentId!.Value, CancellationToken.None));
        AssertEx.Equal(ModelLabExperimentStatus.Failed, record.Status);
        AssertEx.Contains(record.ErrorMessage!, "the file went away", StringComparison.Ordinal);
        AssertEx.False(record.ErrorMessage!.Contains("Exception", StringComparison.Ordinal), record.ErrorMessage);
    }

    /// <summary>A first server start can download the runtime, so the baseline phase must already show by then, and only once.</summary>
    [Test]
    public async Task TheBaselinePhase_IsAnnouncedBeforeTheFirstServerStarts_AndOnlyOnce()
    {
        using var process = new ScriptedProcess();
        var harness = new ScriptedHarness();
        await using var host = await StartHostAsync(process, harness);
        var buffer = host.Services.GetRequiredService<ITrainingRunEventBuffer>();
        var start = await host.Service.StartExperimentAsync(Request(host), CancellationToken.None);
        var id = start.ExperimentId!.Value;
        var phasesAtArm = new List<string[]>();
        harness.BeforeBody = _ =>
        {
            phasesAtArm.Add(buffer.Replay(id, afterSequence: 0).Events
                                  .Where(static item => item.Kind == TrainingRunEventKind.Phase)
                                  .Select(static item => item.Payload.Phase!)
                                  .ToArray());
            return Task.CompletedTask;
        };

        process.Finish(CompleteRun, exitCode: 0);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);

        AssertEx.Equal(expected: 3, phasesAtArm.Count);
        AssertEx.Equal("arm-baseline", phasesAtArm[0][^1], "The body runs after the server is up, which is after any download.");
        AssertEx.Equal(expected: 1, phasesAtArm[2].Count(static phase => phase == "arm-baseline"));
    }

    /// <summary>Refused while nothing is spent: no derive, no gate, no row. A trace starts no server, so it is not asked.</summary>
    [Test]
    public async Task AStartThatWouldHaveNoRuntime_IsRefused_BeforeTheDerive_AndATraceIsNot()
    {
        const string refusal = "No runtime, and the policy refused to fetch one.";
        var manager = Substitute.For<ILlamaCppBinaryManager>();
        manager.GetImplicitAcquisitionRefusalAsync(Arg.Any<GpuVariant>(), Arg.Any<CancellationToken>()).Returns(refusal);
        using var process = new ScriptedProcess();
        await using var host = await StartHostAsync(process, new ScriptedHarness(), configure: services =>
        {
            services.RemoveAll<ILlamaCppBinaryManager>();
            services.AddSingleton(manager);
        });

        var refused = await host.Service.StartExperimentAsync(Request(host), CancellationToken.None);

        AssertEx.Equal(ModelLabStartRefusal.RuntimeUnavailable, refused.Refusal!.Value);
        AssertEx.Equal(refusal, refused.RefusalMessage);
        AssertEx.Null(process.Request);
        AssertEx.Null(host.Gate.ExclusiveKind);
        AssertEx.Equal(expected: 0, (await host.Service.ListAsync(CancellationToken.None)).Count);

        var trace = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);
        AssertEx.Null(trace.Refusal);
        await process.Spawned.WaitAsync(TestBudgets.Contended);
        process.Finish(CompleteRun, exitCode: 0);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);
    }

    /// <summary>An Offline node on a platform with no prebuilt: the launch's own reason, not a server error.</summary>
    [Test]
    public async Task AStartWhoseRuntimeCannotBeResolved_IsRefusedWithTheReason()
    {
        var manager = Substitute.For<ILlamaCppBinaryManager>();
        manager.GetImplicitAcquisitionRefusalAsync(Arg.Any<GpuVariant>(), Arg.Any<CancellationToken>())
               .Returns<Task<string?>>(_ => throw new LlamaRuntimeException("No prebuilt for this host."));
        using var process = new ScriptedProcess();
        await using var host = await StartHostAsync(process, new ScriptedHarness(), configure: services =>
        {
            services.RemoveAll<ILlamaCppBinaryManager>();
            services.AddSingleton(manager);
        });

        var refused = await host.Service.StartExperimentAsync(Request(host), CancellationToken.None);

        AssertEx.Equal(ModelLabStartRefusal.RuntimeUnavailable, refused.Refusal!.Value);
        AssertEx.Equal("No prebuilt for this host.", refused.RefusalMessage);
        AssertEx.Null(process.Request);
    }

    /// <summary>The wait has no watchdog of its own: a GGUF that never verifies would otherwise hold the GPU for good.</summary>
    [Test]
    public async Task AGgufThatNeverVerifies_FailsAtTheJobsBound_AndSaysSo()
    {
        var leases = Substitute.For<ITrainingEvaluationInstalledModelLeaseProvider>();
        leases.AcquireAsync(Gguf, Arg.Any<CancellationToken>()).Returns(async call =>
        {
            await Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>());
            return Lease();
        });
        using var process = new ScriptedProcess();
        await using var host = await StartHostAsync(process, new ScriptedHarness(), leases: leases, options: new ModelLabJobOptions
        {
            MaxDuration = TimeSpan.FromMilliseconds(50)
        });

        var start = await host.Service.StartExperimentAsync(Request(host), CancellationToken.None);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);

        var record = AssertEx.NotNull(await host.Service.GetAsync(start.ExperimentId!.Value, CancellationToken.None));
        AssertEx.Equal(ModelLabExperimentStatus.Failed, record.Status);
        AssertEx.Contains(record.ErrorMessage!, "maximum duration", StringComparison.Ordinal);
        AssertEx.Null(process.Request);
    }

    [Test]
    [Arguments("missing", "not found")]
    [Arguments("unstable", "could not be read")]
    public async Task AGgufThatCannotBeHeld_FailsWithItsReason(string kind, string expected)
    {
        var leases = Substitute.For<ITrainingEvaluationInstalledModelLeaseProvider>();
        leases.AcquireAsync(Gguf, Arg.Any<CancellationToken>()).Returns<Task<ITrainingEvaluationInstalledModelLease>>(_ => kind == "missing"
            ? throw new KeyNotFoundException("The installed model was not found.")
            : throw new InvalidOperationException("InstalledModelSnapshotUnstable"));
        using var process = new ScriptedProcess();
        await using var host = await StartHostAsync(process, new ScriptedHarness(), leases: leases);

        var start = await host.Service.StartExperimentAsync(Request(host), CancellationToken.None);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);

        var record = AssertEx.NotNull(await host.Service.GetAsync(start.ExperimentId!.Value, CancellationToken.None));
        AssertEx.Equal(ModelLabExperimentStatus.Failed, record.Status);
        AssertEx.Contains(record.ErrorMessage!, expected, StringComparison.Ordinal);
        AssertEx.Null(process.Request);
    }

    /// <summary>Each is what an arm can really throw, and each would otherwise end as "failed to run", with its reason only in a log.</summary>
    [Test]
    [Arguments("admission")]
    [Arguments("io")]
    [Arguments("access")]
    public async Task AnArmFailureOfAnyKind_KeepsItsReason_AndTheEarlierArms(string kind)
    {
        Exception thrown = kind switch
        {
            "admission" => new GpuModelLoadAdmissionTimeoutException(),
            "io" => new IOException("hashing stopped"),
            _ => new UnauthorizedAccessException("hashing refused")
        };
        using var process = new ScriptedProcess();
        var harness = new ScriptedHarness();
        await using var host = await StartHostAsync(process, harness);
        harness.BeforeBody = arm => arm == 1 ? Task.FromException(thrown) : Task.CompletedTask;

        var start = await host.Service.StartExperimentAsync(Request(host), CancellationToken.None);
        process.Finish(CompleteRun, exitCode: 0);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);

        var record = AssertEx.NotNull(await host.Service.GetAsync(start.ExperimentId!.Value, CancellationToken.None));
        AssertEx.Equal(ModelLabExperimentStatus.Failed, record.Status);
        AssertEx.Contains(record.ErrorMessage!, thrown.Message, StringComparison.Ordinal);
        AssertEx.Equal("baseline", ReadResult(record.ResultJson).Steering!.Arms.Single().Name);
    }

    [Test]
    public async Task AServerThatAnswersWithNoChoices_FailsTheArm_AndSaysSo()
    {
        using var process = new ScriptedProcess();
        var harness = new ScriptedHarness();
        harness.ArmServer.EmptyOnRequest = 3;
        await using var host = await StartHostAsync(process, harness);

        var start = await host.Service.StartExperimentAsync(Request(host), CancellationToken.None);
        process.Finish(CompleteRun, exitCode: 0);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);

        var record = AssertEx.NotNull(await host.Service.GetAsync(start.ExperimentId!.Value, CancellationToken.None));
        AssertEx.Contains(record.ErrorMessage!, "no choices", StringComparison.Ordinal);
        AssertEx.Equal("baseline", ReadResult(record.ResultJson).Steering!.Arms.Single().Name);
    }

    [Test]
    public async Task TheCheckRoute_NamesTheDefaultLayerAndTheKinds_ForTheForm()
    {
        using var process = new ScriptedProcess();
        await using var host = await StartHostAsync(process, new ScriptedHarness());
        using var client = host.Factory.CreateClient();
        using var request = host.Authorized(HttpMethod.Post, "/api/local/v1/model-lab/checkpoint-preflight");
        request.Content = JsonContent.Create(new
        {
            baseArtifactId = host.Request().BaseArtifactId,
            installedModelName = Gguf
        });

        using var response = await client.SendAsync(request);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        AssertEx.Equal(expected: 15, body.RootElement.GetProperty("defaultSteeringLayer").GetInt32());
        AssertEx.Equal(expected: 24, body.RootElement.GetProperty("steerableBlockCount").GetInt32());
        AssertEx.Equal("full_attention", body.RootElement.GetProperty("checkpointLayerTypes")[15].GetString());
    }

    [Test]
    [Arguments(24, 15)]
    [Arguments(32, 19)]
    public void TheDefaultLayer_IsTheFullAttentionBlockNearestThatDepth(int blocks, int expected)
    {
        var types = Enumerable.Range(0, blocks).Select(static block => block % 4 == 3 ? "full_attention" : "linear_attention").ToArray();

        AssertEx.Equal(expected, ModelLabTraceSpec.DefaultSteeringLayer(blocks, types));
    }

    [Test]
    public void TheDefaultLayer_WithNoKindsNamed_IsJustNearThatDepth_AndNeverBlockZero()
    {
        AssertEx.Equal(expected: 14, ModelLabTraceSpec.DefaultSteeringLayer(24, layerTypes: null));
        AssertEx.Equal(expected: 1, ModelLabTraceSpec.DefaultSteeringLayer(2, ["full_attention", "linear_attention"]));
    }

    private static ModelLabExperimentRequest Request(LabHost host, int? layer = null) =>
        host.Request() with
        {
            Prompt = "first prompt",
            Steering = new ModelLabSteeringRequest
            {
                PositivePrompts = ["kind good"],
                NegativePrompts = ["cruel bad"],
                AdditionalTestPrompts = ["second prompt"],
                Layer = layer,
                Scale = 2.0
            }
        };

    private static ModelLabTraceResult ReadResult(ReadOnlyMemory<byte>? json) =>
        ModelLabTraceSpec.ReadResultOrNull(json!.Value)!;

    private static ITrainingEvaluationInstalledModelLease Lease()
    {
        var lease = Substitute.For<ITrainingEvaluationInstalledModelLease>();
        lease.ModelFilePath.Returns("/models/some.gguf");
        return lease;
    }

    private static Task<LabHost> StartHostAsync(ScriptedProcess process,
        ScriptedHarness harness,
        ITrainingEvaluationInstalledModelLease? lease = null,
        ITrainingEvaluationInstalledModelLeaseProvider? leases = null,
        ModelLabJobOptions? options = null,
        Action<IServiceCollection>? configure = null)
    {
        if (leases is null)
        {
            var held = lease ?? Lease();
            leases = Substitute.For<ITrainingEvaluationInstalledModelLeaseProvider>();
            leases.AcquireAsync(Gguf, Arg.Any<CancellationToken>()).Returns(held);
        }

        return LabHost.StartAsync(process, options, configure: services =>
        {
            services.RemoveAll<ITransientLlamaServerEvaluationHarness>();
            services.AddSingleton<ITransientLlamaServerEvaluationHarness>(harness);
            services.RemoveAll<ITrainingEvaluationInstalledModelLeaseProvider>();
            services.AddSingleton(leases);
            var clients = Substitute.For<IHttpClientFactory>();
            clients.CreateClient(ModelLabArmRunner.HttpClientName).Returns(_ => new HttpClient(harness.ArmServer, disposeHandler: false));
            services.RemoveAll<ModelLabArmRunner>();
            services.AddSingleton(provider => new ModelLabArmRunner(harness, clients, provider.GetRequiredService<ITrainingRunEventBuffer>(), TimeProvider.System));
            configure?.Invoke(services);
        });
    }

    /// <summary>Answers each prompt as a server of the current arm would, and records what it was asked.</summary>
    private sealed class ScriptedArmServer : HttpMessageHandler
    {
        private int _count;

        public List<string> Requests { get; } = [];

        public int CurrentArm { get; set; }

        public int? FailOnRequest { get; set; }

        public int? EmptyOnRequest { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (++_count == FailOnRequest)
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }

            if (_count == EmptyOnRequest)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        choices = Array.Empty<object>()
                    })
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    choices = new[]
                    {
                        new
                        {
                            message = new
                            {
                                content = $"arm {CurrentArm}"
                            },
                            finish_reason = "stop",
                            logprobs = new
                            {
                                content = new[]
                                {
                                    new
                                    {
                                        id = 7
                                    },
                                    new
                                    {
                                        id = 8
                                    },
                                    new
                                    {
                                        id = 9
                                    }
                                }
                            }
                        }
                    }
                })
            };
        }
    }

    /// <summary>Runs the body per arm the way the real one does, with the real one's order and cancellation point, on no process.</summary>
    private sealed class ScriptedHarness : ITransientLlamaServerEvaluationHarness
    {
        public ScriptedArmServer ArmServer { get; } = new();

        public Func<int, Task>? BeforeBody { get; set; }

        public Func<int, Task>? AfterBody { get; set; }

        public int BodiesRun { get; private set; }

        public bool LastExitObserved { get; init; } = true;

        public Task<TransientLlamaServerEvaluationResult<T>> RunAsync<T>(TransientLlamaServerEvaluationRequest request,
            Func<TransientLlamaServerEvaluationProvenance, CancellationToken, Task> bindProvenance,
            Func<TransientLlamaServerEvaluationSession, CancellationToken, Task<T>> body,
            CancellationToken ct) =>
            throw new NotSupportedException("A steering job runs arms.");

        /// <summary>The real one waits on the lease a steering job already holds, so a job reaching this has deadlocked.</summary>
        public Task<IReadOnlyList<TransientLlamaServerEvaluationResult<T>>> RunArmsAsync<T>(IReadOnlyList<TransientLlamaServerEvaluationRequest> requests,
            Func<int, TransientLlamaServerEvaluationSession, CancellationToken, Task<T>> body,
            CancellationToken ct) =>
            throw new InvalidOperationException("A job holding the lease must pass it.");

        public async Task<IReadOnlyList<TransientLlamaServerEvaluationResult<T>>> RunArmsAsync<T>(ILlamaServerRuntimeMutationLease heldLease,
            IReadOnlyList<TransientLlamaServerEvaluationRequest> requests,
            Func<int, TransientLlamaServerEvaluationSession, CancellationToken, Task<T>> body,
            CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(heldLease);
            AssertEx.Equal(expected: 3, requests.Count);
            var results = new List<TransientLlamaServerEvaluationResult<T>>();
            for (var arm = 0; arm < requests.Count; arm++)
            {
                ct.ThrowIfCancellationRequested();
                ArmServer.CurrentArm = arm;
                var vector = requests[arm].ControlVectorPath is { } path
                    ? new TransientLlamaServerControlVector
                    {
                        Layer = 15,
                        ElementCount = 1024,
                        SizeBytes = new FileInfo(path).Length,
                        Sha256 = (await FileSha256.ComputeWithLengthAsync(path, ct)).Sha256,
                        Scale = requests[arm].ControlVectorScale
                    }
                    : null;
                var session = new TransientLlamaServerEvaluationSession
                {
                    BaseAddress = new Uri("http://127.0.0.1:1/"),
                    ModelId = "xe-evaluation-test",
                    Model = Model,
                    Launch = Launch,
                    ControlVector = vector
                };
                if (BeforeBody is not null)
                {
                    await BeforeBody(arm);
                }

                BodiesRun++;
                var value = await body(arm, session, ct);
                if (AfterBody is not null)
                {
                    await AfterBody(arm);
                }

                results.Add(new TransientLlamaServerEvaluationResult<T>
                {
                    Value = value,
                    Model = Model,
                    Launch = Launch,
                    ControlVector = vector,
                    Teardown = new TransientLlamaServerTeardownEvidence
                    {
                        ProcessId = 100 + arm,
                        TreeKillRequested = true,
                        ProcessExitObserved = arm < requests.Count - 1 || LastExitObserved,
                        ExitObservationTimedOut = false,
                        HandleDisposed = true
                    }
                });
            }

            return results;
        }

        private static TransientLlamaServerModelProvenance Model { get; } = new()
        {
            ModelId = "model",
            ModelSizeBytes = 1,
            ModelSha256 = new string('a', 64),
            AdapterId = null,
            AdapterSizeBytes = null,
            AdapterSha256 = null
        };

        private static LlamaServerLaunchReceipt Launch { get; } = new()
        {
            ReceiptVersion = LlamaServerLaunchReceipt.CurrentVersion,
            Variant = GpuVariant.Cuda,
            Os = "linux",
            ExecutableVersion = "b1",
            ExecutableSha256 = new string('e', 64),
            ManifestSha256 = new string('e', 64),
            LaunchProjection = new LlamaServerLaunchProjection
            {
                AutoFit = false,
                Metrics = true,
                ContextTokens = 4096,
                GpuLayers = null,
                TensorSplit = null,
                OverrideTensor = null,
                CpuMoe = false,
                KvCacheTypeK = null,
                KvCacheTypeV = null,
                FlashAttentionMode = LlamaServerLaunchProjection.FlashAttentionAuto,
                Threads = 4,
                ThreadsBatch = 4,
                BatchSize = 512,
                UbatchSize = 512,
                Parallel = 1,
                CacheReuse = null,
                CacheRamMiB = 0,
                Jinja = true,
                Pooling = null
            },
            AuxAssets = new LlamaServerLaunchAuxAssets(HasLora: false, HasMmproj: false, HasDraft: false),
            Placement = new LlamaServerLaunchPlacement(LlamaServerPlacementOutcome.Unknown, null, null),
            EffectiveContextTokens = 4096,
            BenchmarkLaunchPolicy = LlamaServerBenchmarkLaunchPolicy.DeterministicV1
        };
    }
}
