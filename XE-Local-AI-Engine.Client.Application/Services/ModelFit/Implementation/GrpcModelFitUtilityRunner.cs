namespace XE_Local_AI_Engine.Client.Services.ModelFit.Implementation;

using System.Net;
using System.Net.Sockets;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.HostAgent;
using XE_Local_AI_Engine.HostAgent.Grpc.Contracts;
using XE_Local_AI_Engine.HostAgent.Grpc.Contracts.Security;

/// <summary>
///     The HostAgent-backed <see cref="IModelFitUtilityRunner" />: a thin gRPC client to HostAgent's
///     <c>ModelFitUtilityControl</c> service over the same Unix socket and HMAC scheme the lifecycle/sandbox clients use
///     . It owns no Docker — the privileged container work runs in HostAgent.Linux. The runner only
///     translates the intent-level request to the proto message, attaches per-call HMAC metadata, and maps the reply
///     back. There is no command/argv/image-name on the wire — the HostAgent builds the llmfit argv server-side.
/// </summary>
public sealed class GrpcModelFitUtilityRunner : IModelFitUtilityRunner, IDisposable
{
    private const string RunModelFitUtilityMethodName = "/xe.hostagent.v1.ModelFitUtilityControl/RunModelFitUtility";

    private readonly GrpcChannel _channel;
    private readonly ModelFitUtilityControl.ModelFitUtilityControlClient _client;
    private readonly SocketsHttpHandler _handler;
    private readonly HostAgentClientOptions _options;
    private readonly TimeProvider _timeProvider;

    public GrpcModelFitUtilityRunner(HostAgentClientOptions options, TimeProvider timeProvider)
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
        _client = new ModelFitUtilityControl.ModelFitUtilityControlClient(_channel);
    }

    public void Dispose()
    {
        _channel.Dispose();
        _handler.Dispose();
    }

    public async Task<ModelFitUtilityRunResult> RunAsync(ModelFitUtilityRunRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var grpcRequest = ToMessage(request);

        var reply = await _client.RunModelFitUtilityAsync(grpcRequest,
            CreateHeaders(grpcRequest, RunModelFitUtilityMethodName),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return ToResult(reply);
    }

    /// <summary>Maps the intent-level request to the proto message. Visible for unit testing the translation without a live channel.</summary>
    public static RunModelFitUtilityRequest ToMessage(ModelFitUtilityRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new RunModelFitUtilityRequest
        {
            ImageReference = request.ImageReference,
            Operation = ToOperationMessage(request.Operation),
            UseCase = request.UseCase ?? string.Empty,
            Limit = request.Limit,
            ModelName = request.ModelName ?? string.Empty,
            ProviderName = request.ProviderName,
            ProviderUrl = request.ProviderUrl ?? string.Empty,
            Network = request.AttachRuntimeNetwork
                ? ModelFitNetworkModeMessage.ModelFitNetworkModeRuntime
                : ModelFitNetworkModeMessage.ModelFitNetworkModeNone,
            CpuCoresOverride = request.CpuCoresOverride ?? 0,
            RamOverrideGb = request.RamOverrideGb ?? 0,
            VramOverrideGb = request.VramOverrideGb ?? 0,
            TimeoutSeconds = request.TimeoutSeconds ?? 0
        };
    }

    /// <summary>Maps the proto reply to the result record. Visible for unit testing the translation without a live channel.</summary>
    public static ModelFitUtilityRunResult ToResult(RunModelFitUtilityReply reply)
    {
        ArgumentNullException.ThrowIfNull(reply);

        return new ModelFitUtilityRunResult(Status: ToRunStatus(reply.Status),
            ExitCode: reply.ExitCode,
            StandardOutput: reply.StandardOutput,
            StandardError: reply.StandardError,
            Completed: reply.Completed,
            DurationMs: reply.DurationMs,
            StartedAtUtc: reply.StartedAt?.ToDateTimeOffset().ToUnixTimeMilliseconds(),
            CompletedAtUtc: reply.CompletedAt?.ToDateTimeOffset().ToUnixTimeMilliseconds(),
            SanitizedError: string.IsNullOrEmpty(reply.SanitizedError) ? null : reply.SanitizedError);
    }

    private static ModelFitOperationMessage ToOperationMessage(ModelFitOperation operation)
    {
        return operation switch
        {
            ModelFitOperation.Recommend => ModelFitOperationMessage.ModelFitOperationRecommend,
            ModelFitOperation.Benchmark => ModelFitOperationMessage.ModelFitOperationBenchmark,
            _ => ModelFitOperationMessage.ModelFitOperationUnspecified
        };
    }

    private static ModelFitRunStatus ToRunStatus(ModelFitTerminalStatusMessage status)
    {
        return status switch
        {
            ModelFitTerminalStatusMessage.Succeeded => ModelFitRunStatus.Succeeded,
            ModelFitTerminalStatusMessage.Failed => ModelFitRunStatus.Failed,
            ModelFitTerminalStatusMessage.Cancelled => ModelFitRunStatus.Cancelled,
            ModelFitTerminalStatusMessage.TimedOut => ModelFitRunStatus.TimedOut,
            // An unspecified terminal status from the wire is treated as a failure (never silently a success).
            _ => ModelFitRunStatus.Failed
        };
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
}
