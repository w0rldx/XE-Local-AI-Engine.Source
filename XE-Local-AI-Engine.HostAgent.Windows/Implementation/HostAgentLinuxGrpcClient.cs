namespace XE_Local_AI_Engine.HostAgent.Windows.Implementation;

using global::Grpc.Core;
using global::Grpc.Net.Client;
using global::Grpc.Net.Client.Configuration;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.HostAgent.Grpc.Contracts;
using XE_Local_AI_Engine.HostAgent.Grpc.Contracts.Security;

/// <summary>
///     Windows-side gRPC client boundary for Linux HostAgent operations.
/// </summary>
public sealed class HostAgentLinuxGrpcClient : IHostAgentLinuxClient, IDisposable
{
    private const string GetStatusMethodName = "/xe.hostagent.v1.HostAgentControl/GetStatus";
    private const string StartAllContainersMethodName = "/xe.hostagent.v1.HostAgentControl/StartAllContainers";
    private const string StopAllContainersMethodName = "/xe.hostagent.v1.HostAgentControl/StopAllContainers";
    private readonly GrpcChannel _channel;
    private readonly HostAgentControl.HostAgentControlClient _client;
    private readonly ILogger<HostAgentLinuxGrpcClient> _logger;

    private readonly HostAgentLinuxGrpcOptions _options;
    private readonly TimeProvider _timeProvider;

    public HostAgentLinuxGrpcClient(IOptions<HostAgentLinuxGrpcOptions> options,
        TimeProvider timeProvider,
        ILogger<HostAgentLinuxGrpcClient> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _channel = GrpcChannel.ForAddress(_options.EndpointUri, new GrpcChannelOptions
        {
            ServiceConfig = CreateServiceConfig(_options)
        });
        _client = new HostAgentControl.HostAgentControlClient(_channel);
    }

    public void Dispose()
    {
        _channel.Dispose();
    }

    public async Task<HostAgentStatusReply?> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        if (!CanCallLinuxAgent())
        {
            return null;
        }

        var request = new Empty();
        return await CallAsync(() => _client.GetStatusAsync(request, CreateHeaders(request, GetStatusMethodName), cancellationToken: cancellationToken).ResponseAsync,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContainerActionReply?> StartAllContainersAsync(CancellationToken cancellationToken = default)
    {
        if (!CanCallLinuxAgent())
        {
            return null;
        }

        var request = new AllContainersActionRequest();
        return await CallAsync(() => _client.StartAllContainersAsync(request, CreateHeaders(request, StartAllContainersMethodName), cancellationToken: cancellationToken).ResponseAsync,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContainerActionReply?> StopAllContainersAsync(TimeSpan drainTimeout, CancellationToken cancellationToken = default)
    {
        if (!CanCallLinuxAgent())
        {
            return null;
        }

        var request = new AllContainersActionRequest
        {
            DrainTimeoutSeconds = Convert.ToInt32(Math.Ceiling(drainTimeout.TotalSeconds))
        };

        return await CallAsync(() => _client.StopAllContainersAsync(request, CreateHeaders(request, StopAllContainersMethodName), cancellationToken: cancellationToken).ResponseAsync,
            cancellationToken).ConfigureAwait(false);
    }

    internal static ServiceConfig CreateServiceConfig(HostAgentLinuxGrpcOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new ServiceConfig
        {
            MethodConfigs =
            {
                new MethodConfig
                {
                    Names =
                    {
                        MethodName.Default
                    },
                    RetryPolicy = new RetryPolicy
                    {
                        MaxAttempts = options.MaxRetryAttempts,
                        InitialBackoff = options.InitialBackoff,
                        MaxBackoff = options.MaxBackoff,
                        BackoffMultiplier = options.BackoffMultiplier,
                        RetryableStatusCodes =
                        {
                            StatusCode.Unavailable
                        }
                    }
                }
            }
        };
    }

    private async Task<TResponse?> CallAsync<TResponse>(Func<Task<TResponse>> call, CancellationToken cancellationToken)
        where TResponse : class
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (RpcException exception) when (exception.StatusCode == StatusCode.Unavailable)
        {
            _logger.LogInformation("HostAgent.Linux gRPC endpoint is unavailable; gRPC retry policy handled bounded reconnect attempts.");
            return null;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "HostAgent.Linux gRPC call failed.");
            return null;
        }
    }

    private Metadata CreateHeaders(IMessage request, string methodName)
    {
        return HostAgentHmacMetadata.Create(request, methodName, _options.Secret, _timeProvider, _options.BucketSeconds);
    }

    private bool CanCallLinuxAgent()
    {
        if (!string.IsNullOrWhiteSpace(_options.Secret))
        {
            return true;
        }

        _logger.LogWarning("HostAgent.Linux gRPC client is disabled because no HMAC secret is configured.");
        return false;
    }
}
