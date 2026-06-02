namespace XE_Local_AI_Engine.Client.Services.HostAgent.Implementation;

using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using XE_Local_AI_Engine.HostAgent.Abstractions.Contracts;
using XE_Local_AI_Engine.HostAgent.Grpc.Contracts;
using XE_Local_AI_Engine.HostAgent.Grpc.Contracts.Security;
using HostAgentDesiredStateDto = XE_Local_AI_Engine.HostAgent.Abstractions.Contracts.HostAgentDesiredState;
using HostAgentDesiredStateReply = XE_Local_AI_Engine.HostAgent.Grpc.Contracts.HostAgentDesiredState;
using HostAgentStateDto = XE_Local_AI_Engine.HostAgent.Abstractions.Contracts.HostAgentState;
using HostAgentStateReply = XE_Local_AI_Engine.HostAgent.Grpc.Contracts.HostAgentState;
using RuntimeLifecycleDto = XE_Local_AI_Engine.HostAgent.Abstractions.Contracts.RuntimeLifecycle;
using RuntimeLifecycleReply = XE_Local_AI_Engine.HostAgent.Grpc.Contracts.RuntimeLifecycle;
using ContainerDesiredStateDto = XE_Local_AI_Engine.HostAgent.Abstractions.Contracts.ContainerDesiredState;
using ContainerDesiredStateReply = XE_Local_AI_Engine.HostAgent.Grpc.Contracts.ContainerDesiredState;
using ContainerHealthDto = XE_Local_AI_Engine.HostAgent.Abstractions.Contracts.ContainerHealth;
using ContainerHealthReply = XE_Local_AI_Engine.HostAgent.Grpc.Contracts.ContainerHealth;

/// <summary>
///     gRPC client boundary for HostAgent lifecycle, capability, container, and log operations.
/// </summary>
public sealed class GrpcHostAgentClient : IHostAgentClient, IDisposable
{
    private const string GetStatusMethodName = "/xe.hostagent.v1.HostAgentControl/GetStatus";
    private const string GetCapabilitiesMethodName = "/xe.hostagent.v1.HostAgentControl/GetCapabilities";
    private const string ListContainersMethodName = "/xe.hostagent.v1.HostAgentControl/ListContainers";
    private const string StartAllContainersMethodName = "/xe.hostagent.v1.HostAgentControl/StartAllContainers";
    private const string StopAllContainersMethodName = "/xe.hostagent.v1.HostAgentControl/StopAllContainers";
    private const string StartContainerMethodName = "/xe.hostagent.v1.HostAgentControl/StartContainer";
    private const string StopContainerMethodName = "/xe.hostagent.v1.HostAgentControl/StopContainer";
    private const string RestartContainerMethodName = "/xe.hostagent.v1.HostAgentControl/RestartContainer";
    private const string StreamLogsMethodName = "/xe.hostagent.v1.HostAgentControl/StreamLogs";
    private readonly GrpcChannel _channel;
    private readonly HostAgentControl.HostAgentControlClient _client;
    private readonly SocketsHttpHandler _handler;

    private readonly HostAgentClientOptions _options;
    private readonly TimeProvider _timeProvider;

    public GrpcHostAgentClient(HostAgentClientOptions options,
        TimeProvider timeProvider)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.SocketPath);

        var endPoint = new UnixDomainSocketEndPoint(_options.SocketPath);
        _handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancellationToken) => await ConnectAsync(endPoint, cancellationToken).ConfigureAwait(false)
        };
        _channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            HttpHandler = _handler
        });
        _client = new HostAgentControl.HostAgentControlClient(_channel);
    }

    public void Dispose()
    {
        _channel.Dispose();
        _handler.Dispose();
    }

    public async Task<HostAgentStatusDto> GetStatusAsync(CancellationToken cancellationToken)
    {
        var request = new Empty();
        var reply = await _client.GetStatusAsync(request,
            CreateHeaders(request, GetStatusMethodName),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return ToDto(reply);
    }

    public async Task<HostCapabilitiesDto> GetCapabilitiesAsync(CancellationToken cancellationToken)
    {
        var request = new Empty();
        var reply = await _client.GetCapabilitiesAsync(request,
            CreateHeaders(request, GetCapabilitiesMethodName),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return ToDto(reply);
    }

    public async Task<IReadOnlyList<RuntimeComponentStatusDto>> ListContainersAsync(CancellationToken cancellationToken)
    {
        var request = new Empty();
        var reply = await _client.ListContainersAsync(request,
            CreateHeaders(request, ListContainersMethodName),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return reply.Containers.Select(ToDto).ToArray();
    }

    public async Task<ContainerActionReportDto> StartAllContainersAsync(CancellationToken cancellationToken)
    {
        var request = new AllContainersActionRequest();
        var reply = await _client.StartAllContainersAsync(request,
            CreateHeaders(request, StartAllContainersMethodName),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return ToDto(reply);
    }

    public async Task<ContainerActionReportDto> StopAllContainersAsync(TimeSpan drainTimeout, CancellationToken cancellationToken)
    {
        var request = new AllContainersActionRequest
        {
            DrainTimeoutSeconds = ToDrainTimeoutSeconds(drainTimeout)
        };
        var reply = await _client.StopAllContainersAsync(request,
            CreateHeaders(request, StopAllContainersMethodName),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return ToDto(reply);
    }

    public async Task<ContainerActionReportDto> StartContainerAsync(string containerName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);

        var request = new ContainerActionRequest
        {
            ContainerName = containerName
        };
        var reply = await _client.StartContainerAsync(request,
            CreateHeaders(request, StartContainerMethodName),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return ToDto(reply);
    }

    public async Task<ContainerActionReportDto> StopContainerAsync(string containerName, TimeSpan drainTimeout, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);

        var request = new ContainerActionRequest
        {
            ContainerName = containerName,
            DrainTimeoutSeconds = ToDrainTimeoutSeconds(drainTimeout)
        };
        var reply = await _client.StopContainerAsync(request,
            CreateHeaders(request, StopContainerMethodName),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return ToDto(reply);
    }

    public async Task<ContainerActionReportDto> RestartContainerAsync(string containerName, TimeSpan drainTimeout, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);

        var request = new ContainerActionRequest
        {
            ContainerName = containerName,
            DrainTimeoutSeconds = ToDrainTimeoutSeconds(drainTimeout)
        };
        var reply = await _client.RestartContainerAsync(request,
            CreateHeaders(request, RestartContainerMethodName),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return ToDto(reply);
    }

    public IAsyncEnumerable<HostAgentLogLineDto> StreamLogsAsync(string containerName,
        int tailLines,
        bool follow,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        if (tailLines < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tailLines), tailLines, "Tail lines cannot be negative.");
        }

        return StreamLogsCoreAsync(containerName, tailLines, follow, cancellationToken);
    }

    private async IAsyncEnumerable<HostAgentLogLineDto> StreamLogsCoreAsync(string containerName,
        int tailLines,
        bool follow,
        [EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        var request = new StreamLogsRequest
        {
            ContainerName = containerName,
            TailLines = tailLines,
            Follow = follow
        };
        using var call = _client.StreamLogs(request,
            CreateHeaders(request, StreamLogsMethodName),
            cancellationToken: cancellationToken);

        while (await call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false))
        {
            yield return ToDto(call.ResponseStream.Current);
        }
    }

    private Metadata CreateHeaders(IMessage request, string methodName)
    {
        return HostAgentHmacMetadata.Create(request, methodName, _options.Secret, _timeProvider, _options.BucketSeconds);
    }

    private static async ValueTask<Stream> ConnectAsync(EndPoint endPoint, CancellationToken cancellationToken)
    {
#pragma warning disable CA2000 // NetworkStream owns the connected socket on the success path; catch disposes failures.
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(endPoint, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
#pragma warning restore CA2000
    }

    private static int ToDrainTimeoutSeconds(TimeSpan drainTimeout)
    {
        if (drainTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(drainTimeout), drainTimeout, "Drain timeout must be positive.");
        }

        return Convert.ToInt32(Math.Ceiling(drainTimeout.TotalSeconds));
    }

    private static HostAgentStatusDto ToDto(HostAgentStatusReply reply)
    {
        return new HostAgentStatusDto
        {
            State = ToDto(reply.State),
            DesiredState = ToDto(reply.DesiredState),
            RuntimeLifecycle = ToDto(reply.RuntimeLifecycle),
            BootstrapModelReady = reply.BootstrapModelReady,
            WebUiUrl = reply.WebUiUrl,
            ObservedAt = ToDateTimeOffset(reply.ObservedAt),
            Components = reply.Components.Select(ToDto).ToArray(),
            Diagnostics = reply.Diagnostics.ToArray()
        };
    }

    private static HostCapabilitiesDto ToDto(HostCapabilitiesReply reply)
    {
        return new HostCapabilitiesDto
        {
            CpuAvailable = reply.CpuAvailable,
            NvidiaGpuInference = reply.NvidiaGpuInference,
            GpuRuntimeConfigured = reply.GpuRuntimeConfigured,
            AmdGpuStatus = reply.AmdGpuStatus,
            RuntimeDiskBytes = reply.RuntimeDiskBytes,
            ObservedAt = ToDateTimeOffset(reply.ObservedAt),
            Diagnostics = reply.Diagnostics.ToArray()
        };
    }

    private static ContainerActionReportDto ToDto(ContainerActionReply reply)
    {
        return new ContainerActionReportDto
        {
            Action = reply.Action,
            Succeeded = reply.Succeeded,
            StartedAt = ToDateTimeOffset(reply.StartedAt),
            CompletedAt = ToDateTimeOffset(reply.CompletedAt),
            Components = reply.Components.Select(ToDto).ToArray(),
            Diagnostics = reply.Diagnostics.ToArray()
        };
    }

    private static RuntimeComponentStatusDto ToDto(RuntimeComponentStatusReply reply)
    {
        return new RuntimeComponentStatusDto
        {
            Name = reply.Name,
            DesiredState = ToDto(reply.DesiredState),
            Health = ToDto(reply.Health),
            ImageReference = reply.ImageReference,
            DigestVerified = reply.DigestVerified,
            ObservedAt = ToDateTimeOffset(reply.ObservedAt),
            Diagnostics = reply.Diagnostics.ToArray()
        };
    }

    private static HostAgentLogLineDto ToDto(LogEntryReply reply)
    {
        return new HostAgentLogLineDto
        {
            ContainerName = reply.ContainerName,
            Stream = reply.Stream,
            Line = reply.Line,
            ObservedAt = ToDateTimeOffset(reply.ObservedAt)
        };
    }

    private static HostAgentStateDto ToDto(HostAgentStateReply state)
    {
        return state switch
        {
            HostAgentStateReply.Starting => HostAgentStateDto.Starting,
            HostAgentStateReply.Running => HostAgentStateDto.Running,
            HostAgentStateReply.Degraded => HostAgentStateDto.Degraded,
            HostAgentStateReply.Stopping => HostAgentStateDto.Stopping,
            HostAgentStateReply.Stopped => HostAgentStateDto.Stopped,
            HostAgentStateReply.Failed => HostAgentStateDto.Failed,
            _ => HostAgentStateDto.Unknown
        };
    }

    private static HostAgentDesiredStateDto ToDto(HostAgentDesiredStateReply desiredState)
    {
        return desiredState switch
        {
            HostAgentDesiredStateReply.Running => HostAgentDesiredStateDto.Running,
            HostAgentDesiredStateReply.Stopped => HostAgentDesiredStateDto.Stopped,
            _ => HostAgentDesiredStateDto.Stopped
        };
    }

    private static RuntimeLifecycleDto ToDto(RuntimeLifecycleReply lifecycle)
    {
        return lifecycle switch
        {
            RuntimeLifecycleReply.Native => RuntimeLifecycleDto.Native,
            RuntimeLifecycleReply.External => RuntimeLifecycleDto.External,
            _ => RuntimeLifecycleDto.Managed
        };
    }

    private static ContainerDesiredStateDto ToDto(ContainerDesiredStateReply desiredState)
    {
        return desiredState switch
        {
            ContainerDesiredStateReply.Running => ContainerDesiredStateDto.Running,
            ContainerDesiredStateReply.Stopped => ContainerDesiredStateDto.Stopped,
            _ => ContainerDesiredStateDto.Stopped
        };
    }

    private static ContainerHealthDto ToDto(ContainerHealthReply health)
    {
        return health switch
        {
            ContainerHealthReply.Starting => ContainerHealthDto.Starting,
            ContainerHealthReply.Healthy => ContainerHealthDto.Healthy,
            ContainerHealthReply.Unhealthy => ContainerHealthDto.Unhealthy,
            ContainerHealthReply.Stopped => ContainerHealthDto.Stopped,
            _ => ContainerHealthDto.Unknown
        };
    }

    private static DateTimeOffset ToDateTimeOffset(Timestamp? timestamp)
    {
        return timestamp?.ToDateTimeOffset() ?? DateTimeOffset.UnixEpoch;
    }
}
