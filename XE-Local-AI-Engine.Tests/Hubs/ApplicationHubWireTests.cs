namespace XE_Local_AI_Engine.Tests.Hubs;

using System.Buffers;
using System.Text;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client;
using XE_Local_AI_Engine.Client.Hubs;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Images;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Client.Services.Scheduler;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Pins the exact frame text the knowledge-base, image source-build, GGUF download, image-job and scheduler
///     publishers put on the wire: client-method name, property names and casing, value representation and explicit
///     nulls.
/// </summary>
/// <remarks>
///     The React hooks subscribe by these method names and read these property names, so a moved constant or a renamed
///     domain property would stop the UI updating without any error. Each frame goes through a JSON hub protocol
///     configured by <see cref="ConfigureServices.ConfigureJsonSerializerOptions" />, the same call the host's
///     <c>AddJsonProtocol</c> makes. Same shape as <see cref="ProviderStatusHubWireTests" />.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed class ApplicationHubWireTests
{
    private const char RecordSeparator = '\u001e';
    private static readonly Guid FirstId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid SecondId = Guid.Parse("22222222-2222-4222-8222-222222222222");

    [Test]
    public async Task KnowledgeDocumentChangedPush_KeepsItsWireFrame()
    {
        var (hubContext, sent) = CapturingHubContext<KnowledgeBaseHub>();
        var time = new ManualTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(1758000000000));
        var notifier = new KnowledgeIndexingNotifier(hubContext, time, new RecordingLogger<KnowledgeIndexingNotifier>());

        await notifier.NotifyDocumentChangedAsync(FirstId, KnowledgeDocumentStatus.Indexed);

        AssertEx.Equal("{\"type\":1,\"target\":\"knowledge.documentChanged\",\"arguments\":[{\"eventType\":\"knowledge.documentChanged\","
                       + "\"documentId\":\"11111111-1111-4111-8111-111111111111\",\"status\":\"Indexed\",\"occurredAtUtc\":1758000000000}]}"
                       + RecordSeparator,
            WriteFrame(sent.Single().Message));
    }

    [Test]
    public async Task StableDiffusionCppSourceBuildStatusPush_KeepsItsWireFrame()
    {
        var (hubContext, sent) = CapturingHubContext<StableDiffusionCppSourceBuildHub>();
        var publisher = new StableDiffusionCppSourceBuildEventPublisher(hubContext);

        await publisher.PublishStatusAsync(new StableDiffusionCppSourceBuildStatusEvent
        {
            Phase = StableDiffusionCppSourceBuildPhase.SmokeTesting,
            AppendedLogLines = ["cmake ..", "make"],
            AppendedLogStartSequence = 9,
            Terminal = false,
            SanitizedError = null,
            CurrentBuild = new StableDiffusionCppSourceBuildDescriptor
            {
                Backend = SdGpuBackend.Vulkan,
                Source = StableDiffusionCppSourceSelection.Custom,
                Repository = "https://github.com/example/fork",
                RevisionMode = StableDiffusionCppSourceRevisionMode.DefaultBranch,
                RequestedCommit = null,
                ResolvedCommit = new string('a', 40),
                BuildId = FirstId
            }
        });
        await publisher.PublishStatusAsync(new StableDiffusionCppSourceBuildStatusEvent
        {
            Phase = StableDiffusionCppSourceBuildPhase.Failed,
            AppendedLogLines = [],
            AppendedLogStartSequence = 11,
            Terminal = true,
            SanitizedError = "Build failed.",
            CurrentBuild = null
        });

        AssertEx.Equal("{\"type\":1,\"target\":\"stableDiffusionCppSourceBuild.statusChanged\",\"arguments\":[{\"phase\":\"smokeTesting\","
                       + "\"appendedLogLines\":[\"cmake ..\",\"make\"],\"appendedLogStartSequence\":9,\"terminal\":false,\"sanitizedError\":null,"
                       + "\"currentBuild\":{\"buildId\":\"11111111-1111-4111-8111-111111111111\",\"backend\":\"vulkan\",\"source\":\"custom\","
                       + "\"repository\":\"https://github.com/example/fork\",\"revisionMode\":\"defaultBranch\",\"requestedCommit\":null,"
                       + "\"resolvedCommit\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}}]}" + RecordSeparator,
            WriteFrame(sent[0].Message));
        AssertEx.Equal("{\"type\":1,\"target\":\"stableDiffusionCppSourceBuild.statusChanged\",\"arguments\":[{\"phase\":\"failed\","
                       + "\"appendedLogLines\":[],\"appendedLogStartSequence\":11,\"terminal\":true,\"sanitizedError\":\"Build failed.\","
                       + "\"currentBuild\":null}]}" + RecordSeparator,
            WriteFrame(sent[1].Message));
    }

    [Test]
    public async Task GgufDownloadStatusPush_KeepsItsWireFrame()
    {
        var (hubContext, sent) = CapturingHubContext<GgufDownloadHub>();
        var publisher = new GgufDownloadEventPublisher(hubContext);

        await publisher.PublishStatusAsync(new GgufAcquisitionStatus
        {
            ModelName = "unsloth/Qwen3-0.6B-GGUF",
            Phase = GgufAcquisitionPhase.Downloading,
            CompletedBytes = 1024,
            TotalBytes = 4096,
            SanitizedError = null,
            OperationId = FirstId,
            OperationKind = GgufAcquisitionOperationKind.Download,
            ErrorCode = null,
            StartedAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(1758000000000),
            UpdatedAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(1758000000123)
        });
        await publisher.PublishStatusAsync(new GgufAcquisitionStatus
        {
            ModelName = "local/import.gguf",
            Phase = GgufAcquisitionPhase.Failed,
            CompletedBytes = null,
            TotalBytes = null,
            SanitizedError = "Import failed.",
            OperationId = SecondId,
            OperationKind = GgufAcquisitionOperationKind.Import,
            ErrorCode = "import-failed",
            StartedAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(1758000000000),
            UpdatedAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(1758000000456)
        });

        AssertEx.Equal("{\"type\":1,\"target\":\"ggufDownload.statusChanged\",\"arguments\":[{\"modelName\":\"unsloth/Qwen3-0.6B-GGUF\","
                       + "\"phase\":\"Downloading\",\"completedBytes\":1024,\"totalBytes\":4096,\"sanitizedError\":null,"
                       + "\"operationId\":\"11111111-1111-4111-8111-111111111111\",\"operationKind\":\"Download\",\"errorCode\":null,"
                       + "\"updatedAtUtc\":\"2025-09-16T05:20:00.123+00:00\"}]}" + RecordSeparator,
            WriteFrame(sent[0].Message));
        AssertEx.Equal("{\"type\":1,\"target\":\"ggufDownload.statusChanged\",\"arguments\":[{\"modelName\":\"local/import.gguf\","
                       + "\"phase\":\"Failed\",\"completedBytes\":null,\"totalBytes\":null,\"sanitizedError\":\"Import failed.\","
                       + "\"operationId\":\"22222222-2222-4222-8222-222222222222\",\"operationKind\":\"Import\",\"errorCode\":\"import-failed\","
                       + "\"updatedAtUtc\":\"2025-09-16T05:20:00.456+00:00\"}]}" + RecordSeparator,
            WriteFrame(sent[1].Message));
    }

    [Test]
    public async Task ImageJobStatusPush_KeepsItsWireFrameAndGroup()
    {
        var (hubContext, sent) = CapturingHubContext<ImageJobHub>();
        var publisher = new ImageJobEventPublisher(hubContext);

        await publisher.PublishStatusAsync(SamplingImageJobEvent());
        await publisher.PublishStatusAsync(ReconciledImageJobEvent());

        AssertEx.Equal(ImageJobHub.JobGroup(FirstId), sent[0].Group);
        AssertEx.Equal(ImageJobHub.JobGroup(SecondId), sent[1].Group);
        AssertEx.Equal(SamplingImageJobFrame, WriteFrame(sent[0].Message));
        AssertEx.Equal(ReconciledImageJobFrame, WriteFrame(sent[1].Message));
    }

    [Test]
    public async Task ImageJobReplay_KeepsTheLiveWireFrame()
    {
        var coordinator = Substitute.For<IImageJobCoordinator>();
        coordinator.SnapshotBufferedEvents(FirstId).Returns([SamplingImageJobEvent(), ReconciledImageJobEvent()]);
        var sent = new List<InvocationMessage>();
        var caller = Substitute.For<ISingleClientProxy>();
        caller.SendCoreAsync(Arg.Any<string>(), Arg.Any<object?[]>(), Arg.Any<CancellationToken>())
              .Returns(callInfo =>
              {
                  sent.Add(new InvocationMessage(callInfo.ArgAt<string>(0), callInfo.ArgAt<object?[]>(1)));
                  return Task.CompletedTask;
              });
        var clients = Substitute.For<IHubCallerClients>();
        clients.Caller.Returns(caller);
        var context = Substitute.For<HubCallerContext>();
        context.ConnectionId.Returns("conn-1");
        using var hub = new ImageJobHub(coordinator)
        {
            Groups = Substitute.For<IGroupManager>(),
            Clients = clients,
            Context = context
        };

        await hub.Subscribe(FirstId);

        AssertEx.Equal(SamplingImageJobFrame, WriteFrame(sent[0]));
        AssertEx.Equal(ReconciledImageJobFrame, WriteFrame(sent[1]));
    }

    [Test]
    public async Task SchedulerRunPushes_KeepTheirWireFrames()
    {
        var (hubContext, sent) = CapturingHubContext<SchedulerHub>();
        var publisher = new SchedulerEventPublisher(hubContext);
        (SchedulerRunEventKind Kind, string Target)[] kinds =
        [
            (SchedulerRunEventKind.Started, "scheduler.runStarted"),
            (SchedulerRunEventKind.Completed, "scheduler.runCompleted"),
            (SchedulerRunEventKind.Failed, "scheduler.runFailed"),
            (SchedulerRunEventKind.Cancelled, "scheduler.runCancelled")
        ];

        foreach (var (kind, _) in kinds)
        {
            await publisher.PublishRunAsync(new SchedulerRunEvent
            {
                Kind = kind,
                RunId = FirstId,
                ScheduledJobId = SecondId,
                TemplateId = "model-fit-refresh",
                Status = ScheduledRunStatus.Succeeded,
                TriggeredBy = ScheduledRunTrigger.Schedule,
                ScheduledFireTimeUtc = 1758000000000,
                ActualFireTimeUtc = 1758000000100,
                CompletedAtUtc = 1758000000900,
                DurationMs = 800,
                Summary = "Refreshed 3 models.",
                ErrorMessage = null,
                OccurredAtUtc = 1758000001000
            });
        }

        for (var index = 0; index < kinds.Length; index++)
        {
            AssertEx.Equal("{\"type\":1,\"target\":\"" + kinds[index].Target + "\",\"arguments\":[{\"eventType\":\"" + kinds[index].Target
                           + "\",\"runId\":\"11111111-1111-4111-8111-111111111111\",\"scheduledJobId\":\"22222222-2222-4222-8222-222222222222\","
                           + "\"templateId\":\"model-fit-refresh\",\"status\":\"Succeeded\",\"triggeredBy\":\"Schedule\","
                           + "\"scheduledFireTimeUtc\":1758000000000,\"actualFireTimeUtc\":1758000000100,\"completedAtUtc\":1758000000900,"
                           + "\"durationMs\":800,\"summary\":\"Refreshed 3 models.\",\"errorMessage\":null,\"occurredAtUtc\":1758000001000,"
                           + "\"manualFireId\":null}]}"
                           + RecordSeparator,
                WriteFrame(sent[index].Message));
        }
    }

    [Test]
    public async Task SchedulerProgressAndDefinitionPushes_KeepTheirWireFrames()
    {
        var (hubContext, sent) = CapturingHubContext<SchedulerHub>();
        var publisher = new SchedulerEventPublisher(hubContext);

        await publisher.PublishRunProgressAsync(new SchedulerRunProgressEvent
        {
            RunId = FirstId,
            ScheduledJobId = SecondId,
            Message = "Scoring",
            Percent = null,
            OccurredAtUtc = 1758000000000
        });
        await publisher.PublishDefinitionAsync(new SchedulerDefinitionEvent
        {
            ScheduledJobId = SecondId,
            Action = "enabled",
            OccurredAtUtc = 1758000000001
        });

        AssertEx.Equal("{\"type\":1,\"target\":\"scheduler.runProgress\",\"arguments\":[{\"eventType\":\"scheduler.runProgress\","
                       + "\"runId\":\"11111111-1111-4111-8111-111111111111\",\"scheduledJobId\":\"22222222-2222-4222-8222-222222222222\","
                       + "\"message\":\"Scoring\",\"percent\":null,\"occurredAtUtc\":1758000000000}]}" + RecordSeparator,
            WriteFrame(sent[0].Message));
        AssertEx.Equal("{\"type\":1,\"target\":\"scheduler.jobDefinitionChanged\",\"arguments\":[{\"eventType\":\"scheduler.jobDefinitionChanged\","
                       + "\"scheduledJobId\":\"22222222-2222-4222-8222-222222222222\",\"action\":\"enabled\",\"occurredAtUtc\":1758000000001}]}"
                       + RecordSeparator,
            WriteFrame(sent[1].Message));
    }

    private static readonly string SamplingImageJobFrame =
        "{\"type\":1,\"target\":\"imageJob.statusChanged\",\"arguments\":[{\"jobId\":\"11111111-1111-4111-8111-111111111111\","
        + "\"phase\":\"Generating\",\"queuePosition\":null,\"elapsedMs\":1500,\"imageId\":null,\"sanitizedError\":null,"
        + "\"occurredAtUtc\":1758000000000,\"seq\":4,\"generationPhase\":\"Sampling\",\"step\":3,\"totalSteps\":20,"
        + "\"secondsPerIteration\":0.25,\"estimatedRemainingMs\":4250}]}" + RecordSeparator;

    private static readonly string ReconciledImageJobFrame =
        "{\"type\":1,\"target\":\"imageJob.statusChanged\",\"arguments\":[{\"jobId\":\"22222222-2222-4222-8222-222222222222\","
        + "\"phase\":\"Failed\",\"queuePosition\":2,\"elapsedMs\":null,\"imageId\":\"11111111-1111-4111-8111-111111111111\","
        + "\"sanitizedError\":\"Interrupted.\",\"occurredAtUtc\":1758000000001,\"seq\":0,\"generationPhase\":null,\"step\":null,"
        + "\"totalSteps\":null,\"secondsPerIteration\":null,\"estimatedRemainingMs\":null}]}" + RecordSeparator;

    private static ImageJobStatusEvent SamplingImageJobEvent()
    {
        return new ImageJobStatusEvent
        {
            JobId = FirstId,
            Phase = nameof(ImageJobStatus.Generating),
            QueuePosition = null,
            ElapsedMs = 1500,
            ImageId = null,
            SanitizedError = null,
            OccurredAtUtc = 1758000000000,
            Seq = 4,
            GenerationPhase = "Sampling",
            Step = 3,
            TotalSteps = 20,
            SecondsPerIteration = 0.25,
            EstimatedRemainingMs = 4250
        };
    }

    private static ImageJobStatusEvent ReconciledImageJobEvent()
    {
        return new ImageJobStatusEvent
        {
            JobId = SecondId,
            Phase = nameof(ImageJobStatus.Failed),
            QueuePosition = 2,
            ElapsedMs = null,
            ImageId = FirstId,
            SanitizedError = "Interrupted.",
            OccurredAtUtc = 1758000000001,
            Seq = 0
        };
    }

    private static (IHubContext<THub> HubContext, List<(string? Group, InvocationMessage Message)> Sent) CapturingHubContext<THub>()
        where THub : Hub
    {
        var sent = new List<(string? Group, InvocationMessage Message)>();
        // Each proxy is configured before the Returns call that hands it out: configuring a substitute inside another
        // substitute's Returns argument steals NSubstitute's "last call".
        var all = CapturingProxy(group: null, sent);
        var groups = new Dictionary<string, IClientProxy>(StringComparer.Ordinal);
        var clients = Substitute.For<IHubClients>();
        clients.All.Returns(all);
        foreach (var jobId in new[]
                 {
                     FirstId,
                     SecondId
                 })
        {
            var group = ImageJobHub.JobGroup(jobId);
            groups[group] = CapturingProxy(group, sent);
        }

        clients.Group(Arg.Any<string>()).Returns(callInfo => groups[callInfo.ArgAt<string>(0)]);
        var hubContext = Substitute.For<IHubContext<THub>>();
        hubContext.Clients.Returns(clients);
        return (hubContext, sent);
    }

    private static IClientProxy CapturingProxy(string? group, List<(string? Group, InvocationMessage Message)> sent)
    {
        var proxy = Substitute.For<IClientProxy>();
        proxy.SendCoreAsync(Arg.Any<string>(), Arg.Any<object?[]>(), Arg.Any<CancellationToken>())
             .Returns(callInfo =>
             {
                 sent.Add((group, new InvocationMessage(callInfo.ArgAt<string>(0), callInfo.ArgAt<object?[]>(1))));
                 return Task.CompletedTask;
             });
        return proxy;
    }

    private static string WriteFrame(InvocationMessage message)
    {
        var options = new JsonHubProtocolOptions();
        ConfigureServices.ConfigureJsonSerializerOptions(options.PayloadSerializerOptions);
        var buffer = new ArrayBufferWriter<byte>();
        new JsonHubProtocol(Options.Create(options)).WriteMessage(message, buffer);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
