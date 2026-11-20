namespace XE_Local_AI_Engine.Tests.ModelLab;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.ModelLab;
using XE_Local_AI_Engine.Tests.Testing;
using static ModelLabExperimentServiceTests;

/// <summary>One serving worker over a scripted process, with no host or session.</summary>
[Category(TestCategories.Unit)]
public sealed class ModelLabServeWorkerTests
{
    private static readonly ModelLabScriptRunner.ScriptCommand Script = new()
    {
        Interpreter = "python",
        ScriptPath = "modellab.py"
    };

    /// <summary>The worker ignores a cancel for a job it has not been given, so a cancel already requested must follow the run, never precede it.</summary>
    [Test]
    public async Task AJobWhoseTokenIsAlreadyCancelled_WritesRunThenCancel_AndEndsCancelledWithoutAKill()
    {
        using var process = new ScriptedProcess();
        using var workRoot = new TempDirectory("modellab-worker");
        using var worker = ModelLabServeWorker.Spawn(process, new ModelLabScriptRunner.ScriptCommand
        {
            Interpreter = "python",
            ScriptPath = "modellab.py"
        }, workRoot.Path, new ModelLabJobOptions(), TimeProvider.System);
        worker.StartPump();
        AssertEx.Null(await worker.WaitForHandshakeAsync(CancellationToken.None).WaitAsync(TestBudgets.Contended));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var outcome = await worker.RunJobAsync(Guid.NewGuid(), "job.json", static _ => { }, cancelled.Token).WaitAsync(TestBudgets.Contended);

        AssertEx.Equal("run|cancel", string.Join('|', process.WrittenLines.Select(OpOf)));
        AssertEx.True(outcome.Run.State.ScriptCancelled, "The worker acknowledged the cancel of the job it was running.");
        AssertEx.True(outcome.Reusable, "A cancelled job that came back ready keeps its worker.");
        AssertEx.False(process.Killed);
    }

    /// <summary>A break after the job's ready is the idle worker's: the job that already came back clean keeps its success.</summary>
    [Test]
    public async Task ABreakAfterTheJobsReady_IsNotThatJobsFailure()
    {
        using var process = new ScriptedProcess
        {
            // Said at once behind the run: done, ready, then a job frame while idle, which breaks the worker.
            OnRun = static script =>
            {
                script.Say("""{"event":"done","cancelled":false}""");
                script.Say("""{"event":"ready","vramBytes":1000,"reservedBytes":2000}""");
                script.Say("""{"event":"heartbeat","phase":"tracing"}""");
            }
        };
        using var workRoot = new TempDirectory("modellab-worker");
        using var worker = await SpawnReadyAsync(process, workRoot.Path, new ModelLabJobOptions());

        var outcome = await worker.RunJobAsync(Guid.NewGuid(), "job.json", static _ => { }, CancellationToken.None).WaitAsync(TestBudgets.Contended);

        AssertEx.Null(outcome.Run.FailureReason, "The break came after this job's ready.");
        AssertEx.Null(outcome.Run.FailureOrNull(cancelRequested: false), "So the job stands as it finished.");
        await AssertEx.EventuallyAsync(() => process.Killed, TestBudgets.Contended, "The idle break still kills the worker.");
    }

    /// <summary>A break between the job's terminal frame and its ready is that job's: it must not stand as a success.</summary>
    [Test]
    public async Task ABreakAfterTheJobsDone_ButBeforeItsReady_FailsTheJob()
    {
        using var process = new ScriptedProcess
        {
            OnRun = static script =>
            {
                script.Say("""{"event":"done","cancelled":false}""");
                script.Say("""{"event":"done","cancelled":false}""");
            }
        };
        using var workRoot = new TempDirectory("modellab-worker");
        using var worker = await SpawnReadyAsync(process, workRoot.Path, new ModelLabJobOptions());

        var outcome = await worker.RunJobAsync(Guid.NewGuid(), "job.json", static _ => { }, CancellationToken.None).WaitAsync(TestBudgets.Contended);

        AssertEx.Contains(outcome.Run.FailureReason!, "broke its protocol", StringComparison.Ordinal);
        AssertEx.Equal(ModelLabExperimentStatus.Failed, outcome.Run.FailureOrNull(cancelRequested: false)!.Status);
        AssertEx.False(outcome.Reusable);
        AssertEx.True(process.Killed);
    }

    /// <summary>An exit between the job's terminal frame and its ready is no clean end: the job fails and the worker is not reused.</summary>
    [Test]
    public async Task AnExitAfterTheJobsDone_ButBeforeItsReady_FailsTheJob()
    {
        using var process = new ScriptedProcess
        {
            OnRun = static script =>
            {
                script.Say("""{"event":"done","cancelled":false}""");
                script.CloseOutput();
            }
        };
        using var workRoot = new TempDirectory("modellab-worker");
        using var worker = await SpawnReadyAsync(process, workRoot.Path, new ModelLabJobOptions());

        var outcome = await worker.RunJobAsync(Guid.NewGuid(), "job.json", static _ => { }, CancellationToken.None).WaitAsync(TestBudgets.Contended);

        AssertEx.Contains(outcome.Run.FailureReason!, "exited after its job before reporting ready", StringComparison.Ordinal);
        AssertEx.Equal(ModelLabExperimentStatus.Failed, outcome.Run.FailureOrNull(cancelRequested: false)!.Status);
        AssertEx.False(outcome.Reusable);
    }

    /// <summary>An exit after the job's ready is the idle worker's: the job that came back clean keeps its success.</summary>
    [Test]
    public async Task AnExitAfterTheJobsReady_LeavesTheJobSucceeded()
    {
        using var process = new ScriptedProcess
        {
            OnRun = static script =>
            {
                script.Say("""{"event":"done","cancelled":false}""");
                script.Say("""{"event":"ready","vramBytes":1000,"reservedBytes":2000}""");
                script.CloseOutput();
            }
        };
        using var workRoot = new TempDirectory("modellab-worker");
        using var worker = await SpawnReadyAsync(process, workRoot.Path, new ModelLabJobOptions());

        var outcome = await worker.RunJobAsync(Guid.NewGuid(), "job.json", static _ => { }, CancellationToken.None).WaitAsync(TestBudgets.Contended);

        AssertEx.Null(outcome.Run.FailureReason, "The exit came after this job's ready.");
        AssertEx.Null(outcome.Run.FailureOrNull(cancelRequested: false), "So the job stands as it finished.");
    }

    /// <summary>A serve worker's handshake must say it serves; a one-shot script's, or one saying false, breaks the worker before any job.</summary>
    [Test]
    [Arguments("")]
    [Arguments(",\"serve\":false")]
    public async Task AHandshakeThatDoesNotDeclareServe_BreaksTheWorker_AndFailsTheStart(string serveField)
    {
        using var process = new ScriptedProcess
        {
            ServeHandshake = $"{{\"event\":\"handshake\",\"contractVersion\":{ModelLabTraceSpec.ContractVersion}{serveField}}}"
        };
        using var workRoot = new TempDirectory("modellab-worker");
        using var worker = ModelLabServeWorker.Spawn(process, Script, workRoot.Path, new ModelLabJobOptions(), TimeProvider.System);
        worker.StartPump();

        var failed = AssertEx.NotNull(await worker.WaitForHandshakeAsync(CancellationToken.None).WaitAsync(TestBudgets.Contended));

        AssertEx.Contains(failed.FailureReason!, "did not declare serve mode", StringComparison.Ordinal);
        AssertEx.True(process.Killed);
        var outcome = await worker.RunJobAsync(Guid.NewGuid(), "job.json", static _ => { }, CancellationToken.None).WaitAsync(TestBudgets.Contended);
        AssertEx.False(outcome.Reusable, "A broken worker never takes a job.");
        AssertEx.Empty(process.WrittenLines);
    }

    /// <summary>A ready belongs to the process: one stamped with a job, or one without a VRAM count of zero or more, breaks the worker and fails the job.</summary>
    [Test]
    [Arguments("jobId")]
    [Arguments("missing")]
    [Arguments("negative")]
    public async Task AMalformedReady_BreaksTheWorker_AndFailsTheJob(string fault)
    {
        using var process = new ScriptedProcess
        {
            OnRun = script =>
            {
                script.Say("""{"event":"done","cancelled":false}""");
                script.Say(fault switch
                {
                    "jobId" => $"{{\"event\":\"ready\",\"jobId\":\"{script.JobId}\",\"vramBytes\":1000,\"reservedBytes\":2000}}",
                    "missing" => """{"event":"ready","reservedBytes":2000}""",
                    _ => """{"event":"ready","vramBytes":-1,"reservedBytes":2000}"""
                });
            }
        };
        using var workRoot = new TempDirectory("modellab-worker");
        using var worker = await SpawnReadyAsync(process, workRoot.Path, new ModelLabJobOptions());

        var outcome = await worker.RunJobAsync(Guid.NewGuid(), "job.json", static _ => { }, CancellationToken.None).WaitAsync(TestBudgets.Contended);

        AssertEx.Contains(outcome.Run.FailureReason!, "malformed ready", StringComparison.Ordinal);
        AssertEx.Equal(ModelLabExperimentStatus.Failed, outcome.Run.FailureOrNull(cancelRequested: false)!.Status);
        AssertEx.False(outcome.Reusable);
        AssertEx.True(process.Killed);
        AssertEx.Null(worker.VramBytes, "A rejected ready leaves no sample behind.");
    }

    /// <summary>The ready's allocated VRAM is kept for the residents row, from the first ready on and replaced by each later one.</summary>
    [Test]
    public async Task EachReady_KeepsItsAllocatedVram()
    {
        var ready = 0;
        using var process = new ScriptedProcess
        {
            OnRun = script =>
            {
                script.Say("""{"event":"done","cancelled":false}""");
                script.Say($"{{\"event\":\"ready\",\"vramBytes\":{++ready * 1000},\"reservedBytes\":4000}}");
            }
        };
        using var workRoot = new TempDirectory("modellab-worker");
        using var worker = await SpawnReadyAsync(process, workRoot.Path, new ModelLabJobOptions());
        AssertEx.Null(worker.VramBytes, "No ready has come yet.");

        _ = await worker.RunJobAsync(Guid.NewGuid(), "job.json", static _ => { }, CancellationToken.None).WaitAsync(TestBudgets.Contended);
        AssertEx.Equal<long?>(1000, worker.VramBytes);
        var second = await worker.RunJobAsync(Guid.NewGuid(), "job.json", static _ => { }, CancellationToken.None).WaitAsync(TestBudgets.Contended);

        AssertEx.True(second.Reusable);
        AssertEx.Equal<long?>(2000, worker.VramBytes, "The allocated count, never the reserved one.");
    }

    /// <summary>A descendant holding the output pipe neither hides the worker's exit nor keeps feeding lines to the worker being torn down.</summary>
    [Test]
    public async Task Stop_WhenTheOutputOutlivesTheExit_IsVerified_AndRetiresThePumpBeforeReturning()
    {
        using var process = new ScriptedProcess
        {
            OutputOutlivesExit = true
        };
        using var workRoot = new TempDirectory("modellab-worker");
        using var worker = await SpawnReadyAsync(process, workRoot.Path, new ModelLabJobOptions
        {
            EvictGrace = TestBudgets.Contended,
            ExitVerifyTimeout = TestBudgets.Contended
        });

        var exited = await worker.StopAsync().WaitAsync(TestBudgets.Contended);

        AssertEx.True(exited, "The worker exited on its evict; the open pipe does not hide that.");
        AssertEx.True(process.EvictRequested);
        AssertEx.False(process.Killed, "Seen within the grace, so no kill was needed.");
        AssertEx.True(process.OutputReadEnded, "The pump stopped reading before the stop returned.");
    }

    /// <summary>A descendant that never stops writing cannot hold the stop open: the pump checks the stop before each line.</summary>
    [Test]
    public async Task Stop_WhenTheOutputNeverRunsDryAfterTheExit_StillReturns_AndRetiresThePump()
    {
        using var process = new ScriptedProcess
        {
            OutputFloodsAfterExit = true
        };
        using var workRoot = new TempDirectory("modellab-worker");
        using var worker = await SpawnReadyAsync(process, workRoot.Path, new ModelLabJobOptions
        {
            EvictGrace = TestBudgets.Contended,
            ExitVerifyTimeout = TestBudgets.Contended
        });

        var exited = await worker.StopAsync().WaitAsync(TestBudgets.Contended);

        AssertEx.True(exited);
        AssertEx.False(process.Killed, "Seen within the grace, so no kill was needed.");
        AssertEx.True(process.OutputReadEnded, "The pump stopped reading before the stop returned.");
    }

    /// <summary>The spawn's own directory is made before the process; a spawn that throws leaves nothing behind.</summary>
    [Test]
    public void ASpawnThatThrows_DeletesItsWorkerDirectory()
    {
        using var process = new ScriptedProcess
        {
            FailsToSpawn = true
        };
        using var workRoot = new TempDirectory("modellab-worker");

        _ = AssertEx.Throws<InvalidOperationException>(() => ModelLabServeWorker.Spawn(process, Script, workRoot.Path, new ModelLabJobOptions(), TimeProvider.System));

        AssertEx.Equal(Path.Combine(workRoot.Path, "worker-" + process.Request!.RunToken), process.Request.WorkingDirectory);
        AssertEx.False(Directory.Exists(process.Request.WorkingDirectory), "No process started, so its directory went at once.");
        AssertEx.Empty(Directory.GetFileSystemEntries(workRoot.Path));
    }

    private static async Task<ModelLabServeWorker> SpawnReadyAsync(ScriptedProcess process, string workRoot, ModelLabJobOptions options)
    {
        var worker = ModelLabServeWorker.Spawn(process, Script, workRoot, options, TimeProvider.System);
        worker.StartPump();
        AssertEx.Null(await worker.WaitForHandshakeAsync(CancellationToken.None).WaitAsync(TestBudgets.Contended));
        return worker;
    }

    private static string? OpOf(string line)
    {
        using var command = JsonDocument.Parse(line);
        return command.RootElement.GetProperty("op").GetString();
    }
}
