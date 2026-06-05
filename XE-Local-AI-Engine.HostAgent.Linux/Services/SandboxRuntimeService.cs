namespace XE_Local_AI_Engine.HostAgent.Linux.Services;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using global::Docker.DotNet;
using global::Grpc.Core;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using XE_Local_AI_Engine.HostAgent.Grpc.Contracts;
using XE_Local_AI_Engine.HostAgent.Linux.Docker;
using ProtoNetworkMode = XE_Local_AI_Engine.HostAgent.Grpc.Contracts.SandboxNetworkMode;
using SandboxNetworkMode = XE_Local_AI_Engine.HostAgent.Linux.Docker.SandboxNetworkMode;

/// <summary>
///     Serves the <c>SandboxControl</c> gRPC contract by translating proto requests into
///     <see cref="IDockerRuntimeClient" /> sandbox operations and back. The global HMAC interceptor authenticates
///     every call (the rpcs are unary by design), so this service does no auth wiring. It validates the
///     attach key against container labels on attach, redacts host paths from error detail, and surfaces failures as
///     <see cref="RpcException" /> with a sane <see cref="StatusCode" /> the worker maps back onto SPI exceptions.
/// </summary>
public sealed class SandboxRuntimeService : SandboxControl.SandboxControlBase
{
    private const string ContainerNamePrefix = "c0re-agent-home";

    // The non-root account the sandbox process runs as. This name MUST exist in the configured DefaultImage —
    // docker/Dockerfile.agent-home-dotnet creates the "agent" user. Setting it here (not relying solely on the
    // image's baked USER) makes the non-root guarantee explicit and create-time, for the sandbox hardening guarantee.
    private const string NonRootUser = "agent";
    private readonly ILogger<SandboxRuntimeService> _logger;

    private readonly IDockerRuntimeClient _runtimeClient;
    private readonly TimeProvider _timeProvider;

    public SandboxRuntimeService(IDockerRuntimeClient runtimeClient,
        TimeProvider timeProvider,
        ILogger<SandboxRuntimeService> logger)
    {
        _runtimeClient = runtimeClient ?? throw new ArgumentNullException(nameof(runtimeClient));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public override async Task<SandboxHandleReply> CreateOrAttachSandbox(CreateSandboxRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        var attachKey = RequireAttachKey(request.AttachKey);
        var containerName = BuildContainerName(attachKey);

        var existing = await _runtimeClient.FindSandboxContainerAsync(containerName, context.CancellationToken).ConfigureAwait(false);
        var containerId = existing;
        if (containerId is null)
        {
            try
            {
                containerId = await _runtimeClient.CreateSandboxContainerAsync(BuildSpec(request, containerName, attachKey), context.CancellationToken)
                                                  .ConfigureAwait(false);
            }
            catch (DockerApiException exception)
            {
                throw Fault(StatusCode.Internal, "Failed to create the sandbox container.", "create-sandbox", exception);
            }
        }
        else
        {
            // Attaching to an existing container by name is not enough: the name only encodes node + a hash of the
            // owner, so a request with the same owner/node but a different runtime_profile or manifest_version would
            // otherwise reuse a container built under different parameters. Re-validate the reserved labels and reject
            // a mismatch rather than silently reusing.
            await ValidateAttachLabelsAsync(containerId, attachKey, context.CancellationToken).ConfigureAwait(false);
        }

        return BuildHandleReply(containerId, attachKey);
    }

    public override async Task<SandboxHandleReply> ConnectSandbox(ConnectSandboxRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        var attachKey = RequireAttachKey(request.AttachKey);
        var containerName = BuildContainerName(attachKey);

        var containerId = await _runtimeClient.FindSandboxContainerAsync(containerName, context.CancellationToken).ConfigureAwait(false)
                          ?? throw new RpcException(new Status(StatusCode.FailedPrecondition, "No live sandbox matches the supplied attach key."));

        // A name match alone does not authorize the attach — the container's labels must match the
        // full attach key (owner/node/profile/manifest).
        await ValidateAttachLabelsAsync(containerId, attachKey, context.CancellationToken).ConfigureAwait(false);

        return BuildHandleReply(containerId, attachKey);
    }

    public override async Task<ExecuteCommandReply> ExecuteCommand(ExecuteCommandRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireSandboxId(request.SandboxId);

        var execRequest = new DockerExecRequest
        {
            ExecutionId = request.ExecutionId,
            Executable = request.Executable,
            Arguments = request.Arguments.ToArray(),
            WorkingDirectory = string.IsNullOrEmpty(request.WorkingDirectory) ? null : request.WorkingDirectory,
            Environment = request.Environment.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal),
            StandardInput = request.StandardInput.IsEmpty ? ReadOnlyMemory<byte>.Empty : request.StandardInput.Memory,
            Timeout = request.TimeoutSeconds > 0 ? TimeSpan.FromSeconds(request.TimeoutSeconds) : null
        };

        DockerExecResult result;
        try
        {
            result = await _runtimeClient.ExecInContainerAsync(request.SandboxId, execRequest, context.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is DockerApiException or InvalidOperationException)
        {
            throw Fault(StatusCode.FailedPrecondition, "Sandbox is not available for command execution.", "exec", exception);
        }

        return new ExecuteCommandReply
        {
            ExecutionId = result.ExecutionId,
            ExitCode = result.ExitCode,
            StandardOutput = result.StandardOutput,
            StandardError = result.StandardError,
            Completed = result.Completed,
            DurationMs = (long)result.Duration.TotalMilliseconds
        };
    }

    public override async Task<Empty> CopyInto(CopyIntoRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireSandboxId(request.SandboxId);
        if (string.IsNullOrWhiteSpace(request.DestinationPath))
        {
            throw Invalid("A destination path is required.");
        }

        try
        {
            await _runtimeClient.CopyIntoContainerAsync(request.SandboxId,
                request.DestinationPath,
                request.Content.Memory,
                (int)request.FileMode,
                context.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is DockerApiException or InvalidOperationException)
        {
            throw Fault(StatusCode.FailedPrecondition, "Failed to copy the file into the sandbox.", "copy-into", exception);
        }

        return new Empty();
    }

    public override async Task<ReadFileReply> ReadFile(ReadFileRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireSandboxId(request.SandboxId);

        var content = await ReadBytesAsync(request.SandboxId, request.SandboxPath, context.CancellationToken).ConfigureAwait(false);
        return new ReadFileReply
        {
            Content = UnsafeByteOperations.UnsafeWrap(content)
        };
    }

    public override async Task<ReadFileReply> CopyOut(CopyOutRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireSandboxId(request.SandboxId);

        var content = await ReadBytesAsync(request.SandboxId, request.SourcePath, context.CancellationToken).ConfigureAwait(false);
        return new ReadFileReply
        {
            Content = UnsafeByteOperations.UnsafeWrap(content)
        };
    }

    public override Task<Empty> CancelCommand(CancelCommandRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Best-effort cancel: there is no durable server-side exec registry in the unary model, so the
        // cancellation token threaded into ExecuteCommand is the cancellation channel. A standalone CancelCommand is a
        // no-op (the fake provider is also a no-op here) — never throws on a missing execution id.
        return Task.FromResult(new Empty());
    }

    public override async Task<Empty> KillSandbox(KillSandboxRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireSandboxId(request.SandboxId);

        try
        {
            await _runtimeClient.RemoveSandboxContainerAsync(request.SandboxId, context.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is DockerApiException or InvalidOperationException)
        {
            // Killing an already-gone sandbox is success; log the reason at Information and swallow.
            _logger.LogInformation("Kill of sandbox {SandboxId} reported a runtime error (treated as already removed): {Reason}",
                request.SandboxId,
                exception.Message);
        }

        return new Empty();
    }

    private async Task<byte[]> ReadBytesAsync(string sandboxId, string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw Invalid("A path is required.");
        }

        try
        {
            return await _runtimeClient.ReadFromContainerAsync(sandboxId, path, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException exception)
        {
            throw Fault(StatusCode.NotFound, "The requested sandbox path was not found.", "read-file", exception);
        }
        catch (Exception exception) when (exception is DockerApiException or InvalidOperationException)
        {
            throw Fault(StatusCode.FailedPrecondition, "Failed to read the file from the sandbox.", "read-file", exception);
        }
    }

    private SandboxHandleReply BuildHandleReply(string containerId, SandboxAttachKeyMessage attachKey)
    {
        return new SandboxHandleReply
        {
            SandboxId = containerId,
            AttachKey = attachKey.Clone(),
            CreatedAt = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
            ManifestVersion = attachKey.ManifestVersion
        };
    }

    private async Task ValidateAttachLabelsAsync(string containerId, SandboxAttachKeyMessage attachKey, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, string>? labels;
        try
        {
            labels = await _runtimeClient.GetSandboxContainerLabelsAsync(containerId, cancellationToken).ConfigureAwait(false);
        }
        catch (DockerApiException exception)
        {
            throw Fault(StatusCode.FailedPrecondition, "Unable to validate the sandbox attach key.", "validate-attach", exception);
        }

        if (labels is null)
        {
            // The container disappeared between find and validate — treat as no live sandbox.
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "No live sandbox matches the supplied attach key."));
        }

        var expected = new (string Key, string Value)[]
        {
            (SandboxLabelKeys.Owner, attachKey.OwnerUserId),
            (SandboxLabelKeys.Node, attachKey.NodeId),
            (SandboxLabelKeys.Profile, attachKey.RuntimeProfile),
            (SandboxLabelKeys.Manifest, attachKey.ManifestVersion.ToString(CultureInfo.InvariantCulture))
        };

        if (expected.Any(pair => !labels.TryGetValue(pair.Key, out var actual) || !string.Equals(actual, pair.Value, StringComparison.Ordinal)))
        {
            // A name match alone is not authorization: the owner is only hashed into the name, and profile/manifest
            // are not in the name at all, so a same-owner-and-node request with a different profile or manifest would
            // otherwise attach to a container built under different parameters. Reject it. The detail
            // is generic — no label values are echoed across the wire.
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "The attach key does not match the existing sandbox's owner, node, runtime profile, or manifest version."));
        }
    }

    private SandboxContainerSpec BuildSpec(CreateSandboxRequest request, string containerName, SandboxAttachKeyMessage attachKey)
    {
        if (string.IsNullOrWhiteSpace(request.DefaultImage))
        {
            throw Invalid("A sandbox image is required.");
        }

        // observability guard (observability): the create path folds a Restricted request to the no-network default for the current runtime. Log it
        // once so the degradation is observable rather than silent; the enum is retained for future enforcement.
        if (request.Network == ProtoNetworkMode.Restricted)
        {
            _logger.LogInformation("Sandbox '{ContainerName}' requested Restricted network but it was folded to 'none' (restricted egress is not yet enforced).",
                containerName);
        }

        // Apply caller labels first, then stamp the reserved attach-validation labels LAST so a caller can never
        // forge owner/node/profile/manifest via the free-form labels map. These reserved keys are the single source
        // of truth re-read on attach (ValidateAttachLabelsAsync).
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var label in request.Labels)
        {
            labels[label.Key] = label.Value;
        }

        labels[SandboxLabelKeys.Owner] = attachKey.OwnerUserId;
        labels[SandboxLabelKeys.Node] = attachKey.NodeId;
        labels[SandboxLabelKeys.Profile] = attachKey.RuntimeProfile;
        labels[SandboxLabelKeys.Manifest] = attachKey.ManifestVersion.ToString(CultureInfo.InvariantCulture);
        labels[SandboxLabelKeys.Name] = containerName;

        return new SandboxContainerSpec
        {
            Name = containerName,
            Image = request.DefaultImage,
            // The image must define this non-root account (docker/Dockerfile.agent-home-dotnet creates "agent").
            User = NonRootUser,
            CpuCount = request.Limits is { CpuCount: > 0 } ? request.Limits.CpuCount : null,
            MemoryMb = request.Limits is { MemoryMb: > 0 } ? request.Limits.MemoryMb : null,
            PidsLimit = request.Limits is { PidsLimit: > 0 } ? request.Limits.PidsLimit : null,
            NetworkMode = request.Network == ProtoNetworkMode.Restricted
                ? SandboxNetworkMode.Restricted
                : SandboxNetworkMode.None,
            Labels = labels,
            Environment = new Dictionary<string, string>(StringComparer.Ordinal)
        };
    }

    private static string BuildContainerName(SandboxAttachKeyMessage attachKey)
    {
        // Deterministic, filesystem-safe name: prefix + node + a short owner hash. The raw owner is kept
        // on the container labels for attach validation; the name only needs to be stable and collision-resistant.
        var ownerHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(attachKey.OwnerUserId)))[..12];
        var node = Sanitize(attachKey.NodeId);
        return $"{ContainerNamePrefix}-{node}-{ownerHash}";
    }

    private static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            builder.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-');
        }

        return builder.Length == 0 ? "node" : builder.ToString();
    }

    private static SandboxAttachKeyMessage RequireAttachKey(SandboxAttachKeyMessage? attachKey)
    {
        if (attachKey is null
            || string.IsNullOrWhiteSpace(attachKey.OwnerUserId)
            || string.IsNullOrWhiteSpace(attachKey.NodeId))
        {
            throw Invalid("A complete attach key (owner + node) is required.");
        }

        return attachKey;
    }

    private static void RequireSandboxId(string sandboxId)
    {
        if (string.IsNullOrWhiteSpace(sandboxId))
        {
            throw Invalid("A sandbox id is required.");
        }
    }

    // A validation fault carries no internal exception, so it needs no server-side logging — the client message is
    // already the whole story and contains no host paths.
    private static RpcException Invalid(string clientMessage)
    {
        return new RpcException(new Status(StatusCode.InvalidArgument, clientMessage));
    }

    // The client-facing detail is a generic message; the underlying failure is logged at
    // Information (type + message only) so host paths and internal exception detail are never leaked across the wire.
    private RpcException Fault(StatusCode statusCode, string clientMessage, string operation, Exception inner)
    {
        _logger.LogInformation("Sandbox operation '{Operation}' failed: {ExceptionType}: {Reason}",
            operation,
            inner.GetType().Name,
            inner.Message);
        return new RpcException(new Status(statusCode, clientMessage));
    }
}
