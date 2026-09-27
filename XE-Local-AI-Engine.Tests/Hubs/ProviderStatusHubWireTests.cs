namespace XE_Local_AI_Engine.Tests.Hubs;

using System.Buffers;
using System.Text;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client;
using XE_Local_AI_Engine.Client.Hubs;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.Training.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Pins the exact frame text the runtime-acquisition, llama.cpp source-build and training-runtime publishers put on
///     the wire: client-method name, property names and casing, value representation and explicit nulls.
/// </summary>
/// <remarks>
///     The React hooks validate these frames with zod and silently drop one that fails the schema, so a renamed provider
///     property would stop the UI updating without any error. Each frame goes through a JSON hub protocol configured by
///     <see cref="ConfigureServices.ConfigureJsonSerializerOptions" />, the same call the host's <c>AddJsonProtocol</c> makes.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed class ProviderStatusHubWireTests
{
    private const char RecordSeparator = '\u001e';

    [Test]
    public async Task RuntimeAcquisitionStatusPush_KeepsItsWireFrame()
    {
        var (hubContext, sent) = CapturingHubContext<RuntimeAcquisitionHub>();
        var publisher = new RuntimeAcquisitionEventPublisher(hubContext);

        await publisher.PublishStatusAsync(new RuntimeAcquisitionStatusEvent
        {
            Sequence = 7,
            Phase = nameof(RuntimeAcquisitionPhase.Downloading),
            Variant = nameof(GpuVariant.Cuda),
            Tag = "b1234",
            CompletedBytes = 1024,
            TotalBytes = null,
            StepIndex = 1,
            StepCount = 2,
            SanitizedError = null
        });

        AssertEx.Equal(
            "{\"type\":1,\"target\":\"runtimeAcquisition.statusChanged\",\"arguments\":[{\"sequence\":7,\"phase\":\"Downloading\","
            + "\"variant\":\"Cuda\",\"tag\":\"b1234\",\"completedBytes\":1024,\"totalBytes\":null,\"stepIndex\":1,\"stepCount\":2,"
            + "\"sanitizedError\":null}]}" + RecordSeparator,
            WriteFrame(sent.Single()));
    }

    [Test]
    public async Task LlamaCppSourceBuildStatusPush_KeepsItsWireFrame()
    {
        var (hubContext, sent) = CapturingHubContext<LlamaCppSourceBuildHub>();
        var publisher = new LlamaCppSourceBuildEventPublisher(hubContext);

        await publisher.PublishStatusAsync(new LlamaCppSourceBuildStatusEvent
        {
            Phase = "Building",
            AppendedLogLines = ["cmake ..", "make"],
            AppendedLogStartSequence = 41,
            Terminal = false,
            SanitizedError = null,
            CurrentBuild = new LlamaCppSourceBuildDescriptor
            {
                Variant = GpuVariant.Vulkan,
                Source = LlamaCppSourceSelection.Custom,
                Repository = "https://github.com/example/fork",
                RevisionMode = LlamaCppSourceRevisionMode.ExplicitCommit,
                RequestedCommit = new string('a', 40),
                ResolvedCommit = null,
                BuildId = Guid.Parse("11111111-1111-4111-8111-111111111111")
            }
        });
        await publisher.PublishStatusAsync(new LlamaCppSourceBuildStatusEvent
        {
            Phase = "Failed",
            AppendedLogLines = [],
            AppendedLogStartSequence = 43,
            Terminal = true,
            SanitizedError = "Build failed.",
            CurrentBuild = null
        });

        AssertEx.Equal(
            "{\"type\":1,\"target\":\"llamaCppSourceBuild.statusChanged\",\"arguments\":[{\"phase\":\"Building\","
            + "\"appendedLogLines\":[\"cmake ..\",\"make\"],\"appendedLogStartSequence\":41,\"terminal\":false,\"sanitizedError\":null,"
            + "\"currentBuild\":{\"buildId\":\"11111111-1111-4111-8111-111111111111\",\"backend\":\"vulkan\",\"source\":\"custom\","
            + "\"repository\":\"https://github.com/example/fork\",\"revisionMode\":\"explicitCommit\","
            + "\"requestedCommit\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"resolvedCommit\":null}}]}" + RecordSeparator,
            WriteFrame(sent[0]));
        AssertEx.Equal(
            "{\"type\":1,\"target\":\"llamaCppSourceBuild.statusChanged\",\"arguments\":[{\"phase\":\"Failed\","
            + "\"appendedLogLines\":[],\"appendedLogStartSequence\":43,\"terminal\":true,\"sanitizedError\":\"Build failed.\","
            + "\"currentBuild\":null}]}" + RecordSeparator,
            WriteFrame(sent[1]));
    }

    [Test]
    public async Task TrainingRuntimeStatusPush_KeepsItsWireFrame()
    {
        var (hubContext, sent) = CapturingHubContext<TrainingRuntimeHub>();
        var publisher = new TrainingRuntimeEventPublisher(hubContext);

        await publisher.PublishStatusAsync(new TrainingRuntimeStatusEvent
        {
            Phase = nameof(TrainingRuntimePhase.InstallingPackages),
            AppendedLogLines = ["Resolved 42 packages"],
            AppendedLogStartSequence = 3,
            Terminal = false,
            SanitizedError = null
        });

        AssertEx.Equal(
            "{\"type\":1,\"target\":\"trainingRuntime.statusChanged\",\"arguments\":[{\"phase\":\"InstallingPackages\","
            + "\"appendedLogLines\":[\"Resolved 42 packages\"],\"appendedLogStartSequence\":3,\"terminal\":false,"
            + "\"sanitizedError\":null}]}" + RecordSeparator,
            WriteFrame(sent.Single()));
    }

    private static (IHubContext<THub> HubContext, List<InvocationMessage> Sent) CapturingHubContext<THub>()
        where THub : Hub
    {
        var sent = new List<InvocationMessage>();
        var proxy = Substitute.For<IClientProxy>();
        proxy.SendCoreAsync(Arg.Any<string>(), Arg.Any<object?[]>(), Arg.Any<CancellationToken>())
             .Returns(callInfo =>
             {
                 sent.Add(new InvocationMessage(callInfo.ArgAt<string>(0), callInfo.ArgAt<object?[]>(1)));
                 return Task.CompletedTask;
             });
        var clients = Substitute.For<IHubClients>();
        clients.All.Returns(proxy);
        var hubContext = Substitute.For<IHubContext<THub>>();
        hubContext.Clients.Returns(clients);
        return (hubContext, sent);
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
