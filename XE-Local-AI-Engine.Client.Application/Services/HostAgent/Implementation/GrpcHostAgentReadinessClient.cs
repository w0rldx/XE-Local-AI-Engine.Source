namespace XE_Local_AI_Engine.Client.Services.HostAgent.Implementation;

using System.Net;
using System.Net.Sockets;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using XE_Local_AI_Engine.HostAgent.Grpc.Contracts;
using XE_Local_AI_Engine.HostAgent.Grpc.Contracts.Security;

public sealed class GrpcHostAgentReadinessClient : IHostAgentReadinessClient, IDisposable
{
    private const string GetStatusMethodName = "/xe.hostagent.v1.HostAgentControl/GetStatus";
    private readonly GrpcChannel _channel;
    private readonly HostAgentControl.HostAgentControlClient _client;
    private readonly SocketsHttpHandler _handler;
    private readonly ILogger<GrpcHostAgentReadinessClient> _logger;
    private readonly HostAgentStartupGateOptions _options;
    private readonly TimeProvider _timeProvider;

    public GrpcHostAgentReadinessClient(HostAgentStartupGateOptions options,
        TimeProvider timeProvider,
        ILogger<GrpcHostAgentReadinessClient> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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

    public async Task<bool> IsBootstrapModelReadyAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.Secret))
        {
            _logger.LogWarning("HostAgent startup gate is enabled but no HMAC secret is configured.");
            return false;
        }

        try
        {
            var request = new Empty();
            var status = await _client.GetStatusAsync(request,
                CreateHeaders(request, GetStatusMethodName),
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return status.BootstrapModelReady;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogInformation(exception, "HostAgent bootstrap readiness check failed; WorkerHub connection remains gated.");
            return false;
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
}
