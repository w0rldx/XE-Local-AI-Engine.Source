namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Options;
using Microsoft.Win32.SafeHandles;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.HostAgent;
using XE_Local_AI_Engine.HostAgent.Grpc.Contracts;
using XE_Local_AI_Engine.HostAgent.Grpc.Contracts.Security;

/// <summary>
///     The local-container sandbox <see cref="ISandboxRuntimeProvider" />: a thin gRPC client to HostAgent's
///     <c>SandboxControl</c> service over the same Unix socket and HMAC scheme the lifecycle client uses (AgentHome
///     plan §5.2). It owns no Docker — the privileged container work runs in HostAgent.Linux. The provider only
///     translates the provider-neutral SPI DTOs to/from the proto messages, attaches per-call HMAC metadata, and (in
///     <see cref="CopyIntoAsync" />) reads the host file under the no-follow / byte-recheck guards that the fake could
///     not model. Copy carries bytes, never host paths (D3), so HostAgent never touches a
///     selected folder.
/// </summary>
public sealed class LocalContainerSandboxProvider : ISandboxRuntimeProvider, IDisposable
{
    /// <summary>The provider name this registers under for configuration-bound selection.</summary>
    public const string Name = "local-container";

    private const string CreateOrAttachMethodName = "/xe.hostagent.v1.SandboxControl/CreateOrAttachSandbox";
    private const string ConnectMethodName = "/xe.hostagent.v1.SandboxControl/ConnectSandbox";
    private const string ExecuteCommandMethodName = "/xe.hostagent.v1.SandboxControl/ExecuteCommand";
    private const string CopyIntoMethodName = "/xe.hostagent.v1.SandboxControl/CopyInto";
    private const string ReadFileMethodName = "/xe.hostagent.v1.SandboxControl/ReadFile";
    private const string CopyOutMethodName = "/xe.hostagent.v1.SandboxControl/CopyOut";
    private const string CancelCommandMethodName = "/xe.hostagent.v1.SandboxControl/CancelCommand";
    private const string KillSandboxMethodName = "/xe.hostagent.v1.SandboxControl/KillSandbox";

    // Whole-file copy-into (D4) plus protocol headroom: the channel must accept a message larger than the per-file cap.
    private const int CopyMessageHeadroomBytes = 4 * 1024 * 1024;

    // The default copy-into file mode (rw-r--r--); HostAgent reapplies the non-root sandbox owner on extract.
    private const uint DefaultCopyFileMode = 0b110_100_100;

    private readonly GrpcChannel _channel;
    private readonly SandboxControl.SandboxControlClient _client;
    private readonly SocketsHttpHandler _handler;
    private readonly HostAgentClientOptions _hostAgentOptions;
    private readonly ILogger<LocalContainerSandboxProvider> _logger;
    private readonly LocalContainerOptions _options;
    private readonly TimeProvider _timeProvider;

    public LocalContainerSandboxProvider(
        HostAgentClientOptions hostAgentOptions,
        IOptions<LocalContainerOptions> options,
        TimeProvider timeProvider,
        ILogger<LocalContainerSandboxProvider> logger)
    {
        _hostAgentOptions = hostAgentOptions ?? throw new ArgumentNullException(nameof(hostAgentOptions));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ArgumentException.ThrowIfNullOrWhiteSpace(_hostAgentOptions.SocketPath);

        var endPoint = new UnixDomainSocketEndPoint(_hostAgentOptions.SocketPath);
        _handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancellationToken) => await ConnectAsync(endPoint, cancellationToken).ConfigureAwait(false)
        };

        // A whole-file copy-into message (D4) can approach the per-file cap, so the channel limits must clear it plus
        // protocol framing headroom; the same ceiling bounds copy-out / read-file replies.
        var maxMessageSize = checked((int)Math.Min(_options.MaxCopyFileBytes + CopyMessageHeadroomBytes, int.MaxValue));
        _channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            HttpHandler = _handler,
            MaxSendMessageSize = maxMessageSize,
            MaxReceiveMessageSize = maxMessageSize
        });
        _client = new SandboxControl.SandboxControlClient(_channel);
    }

    public string ProviderName => Name;

    public SandboxProviderCapabilities Capabilities =>
        SandboxProviderCapabilities.SupportsCopyInto
        | SandboxProviderCapabilities.SupportsCopyOut
        | SandboxProviderCapabilities.SupportsCommandCancellation
        | SandboxProviderCapabilities.SupportsAttach
        | SandboxProviderCapabilities.SupportsKill
        | SandboxProviderCapabilities.SupportsResourceLimits
        | SandboxProviderCapabilities.SupportsNetworkPolicy;

    public void Dispose()
    {
        _channel.Dispose();
        _handler.Dispose();
    }

    public async Task<SandboxHandle> CreateOrAttachAsync(SandboxCreateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var grpcRequest = new CreateSandboxRequest
        {
            AttachKey = ToMessage(request.AttachKey),
            RuntimeProfile = request.RuntimeProfile,
            DefaultImage = _options.DefaultImage,
            Limits = ToLimitsMessage(request.ResourceLimits),
            Network = ToNetworkMode(request.NetworkPolicy)
        };
        grpcRequest.Labels.Add(BuildLabels(request.AttachKey, request.Labels));

        var reply = await _client.CreateOrAttachSandboxAsync(grpcRequest,
            CreateHeaders(grpcRequest, CreateOrAttachMethodName),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return ToHandle(reply);
    }

    public async Task<SandboxHandle> ConnectAsync(SandboxAttachKey attachKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attachKey);

        var grpcRequest = new ConnectSandboxRequest { AttachKey = ToMessage(attachKey) };
        try
        {
            var reply = await _client.ConnectSandboxAsync(grpcRequest,
                CreateHeaders(grpcRequest, ConnectMethodName),
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return ToHandle(reply);
        }
        catch (RpcException exception) when (IsHandleInvalid(exception.StatusCode))
        {
            throw ToHandleInvalid(exception);
        }
    }

    public async Task<SandboxCommandResult> ExecuteAsync(SandboxHandle handle, SandboxCommandRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(request);

        var grpcRequest = new ExecuteCommandRequest
        {
            SandboxId = handle.SandboxId,
            ExecutionId = request.ExecutionId,
            Executable = request.Executable,
            WorkingDirectory = request.WorkingDirectory ?? string.Empty,
            TimeoutSeconds = ToTimeoutSeconds(request.Timeout),
            StandardInput = request.StandardInput is null
                ? ByteString.Empty
                : ByteString.CopyFrom(request.StandardInput, Encoding.UTF8)
        };
        grpcRequest.Arguments.Add(request.Arguments);
        if (request.Environment is not null)
        {
            foreach (var pair in request.Environment)
            {
                grpcRequest.Environment[pair.Key] = pair.Value;
            }
        }

        try
        {
            var reply = await _client.ExecuteCommandAsync(grpcRequest,
                CreateHeaders(grpcRequest, ExecuteCommandMethodName),
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return ToCommandResult(reply);
        }
        catch (RpcException exception) when (exception.StatusCode == StatusCode.Cancelled && cancellationToken.IsCancellationRequested)
        {
            // A cancelled gRPC call when the caller token fired is a cancellation, not a command failure. Surface an
            // OperationCanceledException so AgentHomeService.RunAsync can disambiguate caller-cancel from timeout — the
            // same shape the fake produces when its in-flight task is cancelled. (Completed=false/ExitCode=-1 is then
            // synthesized by RunAsync's timeout branch.)
            throw new OperationCanceledException(cancellationToken);
        }
        catch (RpcException exception) when (IsHandleInvalid(exception.StatusCode))
        {
            throw ToHandleInvalid(exception);
        }
    }

    public async Task CopyIntoAsync(SandboxHandle handle, SandboxCopyRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(request);

        // The TOCTOU guard: the pass-1 workspace walk already resolved and size-checked this
        // host path, but a host-side swap between the walk and this read could point it at a symlink that escapes the
        // selected folder, or grow the file past the per-file cap. Re-open under no-follow and re-check bytes here —
        // never trust the pass-1 path string.
        var content = ReadHostFileUnderGuard(request.SourcePath);
        if (content is null)
        {
            // Blocked on re-read (§7.1.2): the file is over the per-file cap or grew after the pass-1 sizing. Skip and
            // log — never copy a truncated or over-budget file (D4).
            _logger.LogWarning(
                "Copy-into skipped: a selected file exceeded the {Cap}-byte per-file cap or grew after sizing on re-read.",
                _options.MaxCopyFileBytes);
            return;
        }

        var grpcRequest = new CopyIntoRequest
        {
            SandboxId = handle.SandboxId,
            DestinationPath = request.DestinationPath,
            Content = UnsafeByteOperations.UnsafeWrap(content),
            FileMode = DefaultCopyFileMode
        };

        try
        {
            await _client.CopyIntoAsync(grpcRequest,
                CreateHeaders(grpcRequest, CopyIntoMethodName),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (RpcException exception) when (IsHandleInvalid(exception.StatusCode))
        {
            throw ToHandleInvalid(exception);
        }
    }

    public async Task<string> ReadFileAsync(SandboxHandle handle, string sandboxPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentException.ThrowIfNullOrWhiteSpace(sandboxPath);

        var grpcRequest = new ReadFileRequest { SandboxId = handle.SandboxId, SandboxPath = sandboxPath };
        try
        {
            var reply = await _client.ReadFileAsync(grpcRequest,
                CreateHeaders(grpcRequest, ReadFileMethodName),
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return reply.Content.ToStringUtf8();
        }
        catch (RpcException exception) when (IsHandleInvalid(exception.StatusCode))
        {
            throw ToHandleInvalid(exception);
        }
    }

    public async Task CopyOutAsync(SandboxHandle handle, SandboxCopyRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(request);

        var grpcRequest = new CopyOutRequest { SandboxId = handle.SandboxId, SourcePath = request.SourcePath };
        ReadFileReply reply;
        try
        {
            reply = await _client.CopyOutAsync(grpcRequest,
                CreateHeaders(grpcRequest, CopyOutMethodName),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (RpcException exception) when (IsHandleInvalid(exception.StatusCode))
        {
            throw ToHandleInvalid(exception);
        }

        // Write the raw bytes to the host destination so a binary artifact survives the round trip unchanged.
        await File.WriteAllBytesAsync(request.DestinationPath, reply.Content.ToByteArray(), cancellationToken).ConfigureAwait(false);
    }

    public async Task CancelCommandAsync(SandboxHandle handle, string executionId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

        var grpcRequest = new CancelCommandRequest { SandboxId = handle.SandboxId, ExecutionId = executionId };
        try
        {
            await _client.CancelCommandAsync(grpcRequest,
                CreateHeaders(grpcRequest, CancelCommandMethodName),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (RpcException)
        {
            // Best-effort cancel: a missing execution id or an already-gone sandbox is a no-op, matching the fake.
        }
    }

    public async Task KillAsync(SandboxHandle handle, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);

        var grpcRequest = new KillSandboxRequest { SandboxId = handle.SandboxId };
        try
        {
            await _client.KillSandboxAsync(grpcRequest,
                CreateHeaders(grpcRequest, KillSandboxMethodName),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (RpcException exception) when (IsHandleInvalid(exception.StatusCode))
        {
            throw ToHandleInvalid(exception);
        }
    }

    /// <summary>
    ///     Reads the host file under the no-follow / byte-recheck guards. Returns the bytes, or
    ///     <see langword="null" /> when the file exceeds the per-file cap on this re-read (so the caller blocks the
    ///     copy). Throws <see cref="AgentHomeRequestRejectedException" /> when the final path component is a symlink or
    ///     the open cannot be performed safely — a swap-after-walk attack signal.
    /// </summary>
    private byte[]? ReadHostFileUnderGuard(string sourcePath)
    {
        // No-follow open: refuse a symlink at the final component so a file swapped for a symlink after the pass-1
        // walk cannot redirect the read outside the selected folder. The open targets the leaf atomically; the
        // resolved pass-1 ancestor directories are already canonicalized by the workspace walk.
        var fileHandle = OpenNoFollow(sourcePath);

        using (fileHandle)
        {
            var length = RandomAccess.GetLength(fileHandle);

            // Byte-recheck: a file that grew past the per-file cap after the pass-1 sizing is blocked, not over-copied.
            if (length > _options.MaxCopyFileBytes)
            {
                return null;
            }

            var content = new byte[length];
            var read = 0;
            while (read < content.Length)
            {
                var chunk = RandomAccess.Read(fileHandle, content.AsSpan(read), read);
                if (chunk == 0)
                {
                    // The file shrank after the length read; copy only what is actually present.
                    return content[..read];
                }

                read += chunk;
            }

            // Growth-after-sizing check (§7.1.2): the buffer was sized from the pass-1 length. If even one byte remains
            // past it, the file grew between the length read and the copy. Block (return null) rather than silently
            // truncate to the stale size — parity with the over-cap branch. A single probe byte is enough to detect it.
            Span<byte> probe = stackalloc byte[1];
            if (RandomAccess.Read(fileHandle, probe, length) > 0)
            {
                return null;
            }

            return content;
        }
    }

    /// <summary>
    ///     Opens the host file refusing a symlink at the final component. On Linux this is an atomic <c>open(2)</c> with
    ///     <c>O_NOFOLLOW</c> (the kernel fails with <c>ELOOP</c> if the leaf is a symlink), which closes the
    ///     check-then-open race a managed <c>lstat</c> + open would leave. On a non-Linux host the J-local provider is
    ///     not the target runtime, so it falls back to a plain open and relies on the canonicalized pass-1 ancestor walk
    ///     plus the byte re-check. Throws <see cref="AgentHomeRequestRejectedException" /> when the leaf is a symlink or
    ///     the open otherwise fails — a swap-after-walk attack signal.
    /// </summary>
    private static SafeFileHandle OpenNoFollow(string sourcePath)
    {
        if (!OperatingSystem.IsLinux())
        {
            try
            {
                return File.OpenHandle(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            catch (IOException exception)
            {
                throw new AgentHomeRequestRejectedException(
                    "a selected file could not be opened safely for copy.", exception);
            }
            catch (UnauthorizedAccessException exception)
            {
                throw new AgentHomeRequestRejectedException(
                    "a selected file could not be opened safely for copy (access denied).", exception);
            }
        }

        // Null-terminate the UTF-8 path for libc.
        var pathBytes = new byte[Encoding.UTF8.GetByteCount(sourcePath) + 1];
        Encoding.UTF8.GetBytes(sourcePath, pathBytes);
        var fileDescriptor = open(pathBytes, ReadOnlyNoFollowCloseOnExecFlags);
        if (fileDescriptor < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            throw new AgentHomeRequestRejectedException(string.Create(CultureInfo.InvariantCulture,
                $"a selected file could not be opened safely for copy (it may have been replaced by a link; errno {error})."));
        }

        return new SafeFileHandle((IntPtr)fileDescriptor, ownsHandle: true);
    }

    // O_RDONLY (0x0) | O_NOFOLLOW (0x20000) | O_CLOEXEC (0x80000) on Linux.
    private const int ReadOnlyNoFollowCloseOnExecFlags = 0x0 | 0x20000 | 0x80000;

    // A single libc open(). The path is marshalled by the caller into a null-terminated UTF-8 byte array so any
    // filename (incl. non-ASCII) round-trips correctly; the import takes the raw bytes. DllImport (not source-generated
    // LibraryImport) keeps the project free of AllowUnsafeBlocks — the source generator buys nothing for one call.
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int open(byte[] pathname, int flags);

    private Metadata CreateHeaders(IMessage request, string methodName)
    {
        return HostAgentHmacMetadata.Create(request, methodName, _hostAgentOptions.Secret, _timeProvider, _hostAgentOptions.BucketSeconds);
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

    private Dictionary<string, string> BuildLabels(SandboxAttachKey attachKey, IReadOnlyDictionary<string, string>? extra)
    {
        // The raw owner/node/profile/manifest travel as labels so HostAgent validates an attach by value, even though
        // the container name hashes the owner to stay filesystem-safe. Caller labels are merged but cannot override the
        // reserved attach labels.
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        if (extra is not null)
        {
            foreach (var pair in extra)
            {
                labels[pair.Key] = pair.Value;
            }
        }

        // Reference the shared SandboxLabelKeys consts (in HostAgent.Grpc.Contracts) so the provider and the
        // authoritative HostAgent SandboxRuntimeService stamp byte-identical reserved keys — no divergent spellings.
        labels[SandboxLabelKeys.Owner] = attachKey.OwnerUserId;
        labels[SandboxLabelKeys.Node] = attachKey.NodeId;
        labels[SandboxLabelKeys.Profile] = attachKey.RuntimeProfile;
        labels[SandboxLabelKeys.Manifest] = attachKey.ManifestVersion.ToString(CultureInfo.InvariantCulture);
        labels[SandboxLabelKeys.Name] = BuildContainerName(attachKey);
        return labels;
    }

    /// <summary>
    ///     The deterministic sandbox-container name: <c>{prefix}-{nodeId}-{ownerHash}</c>. The owner is hashed so the
    ///     name stays filesystem/Docker-safe while the raw owner travels as a label for attach validation.
    /// </summary>
    private string BuildContainerName(SandboxAttachKey attachKey)
    {
        var ownerHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(attachKey.OwnerUserId)))[..16];
        return string.Create(CultureInfo.InvariantCulture, $"{_options.ContainerNamePrefix}-{attachKey.NodeId}-{ownerHash}");
    }

    private static SandboxAttachKeyMessage ToMessage(SandboxAttachKey attachKey)
    {
        return new SandboxAttachKeyMessage
        {
            OwnerUserId = attachKey.OwnerUserId,
            NodeId = attachKey.NodeId,
            ProviderName = attachKey.ProviderName,
            RuntimeProfile = attachKey.RuntimeProfile,
            ManifestVersion = attachKey.ManifestVersion
        };
    }

    private static SandboxAttachKey ToAttachKey(SandboxAttachKeyMessage message)
    {
        return new SandboxAttachKey
        {
            OwnerUserId = message.OwnerUserId,
            NodeId = message.NodeId,
            ProviderName = message.ProviderName,
            RuntimeProfile = message.RuntimeProfile,
            ManifestVersion = message.ManifestVersion
        };
    }

    private ResourceLimitsMessage ToLimitsMessage(SandboxResourceLimits? limits)
    {
        // The create request always carries a limits message; the provider's own ceiling fills the gaps the neutral
        // request left unspecified (a zero field means "not specified" on the wire).
        return new ResourceLimitsMessage
        {
            CpuCount = limits?.CpuCount ?? _options.CpuLimit,
            MemoryMb = limits?.MemoryMb ?? _options.MemoryLimitMb,
            PidsLimit = limits?.PidsLimit ?? _options.PidsLimit
        };
    }

    private static SandboxNetworkMode ToNetworkMode(SandboxNetworkPolicy policy)
    {
        return policy switch
        {
            SandboxNetworkPolicy.Restricted => SandboxNetworkMode.Restricted,
            _ => SandboxNetworkMode.None
        };
    }

    private SandboxHandle ToHandle(SandboxHandleReply reply)
    {
        return new SandboxHandle
        {
            ProviderName = Name,
            SandboxId = reply.SandboxId,
            AttachKey = ToAttachKey(reply.AttachKey),
            CreatedAt = reply.CreatedAt?.ToDateTimeOffset() ?? _timeProvider.GetUtcNow(),
            ManifestVersion = reply.ManifestVersion
        };
    }

    private static SandboxCommandResult ToCommandResult(ExecuteCommandReply reply)
    {
        return new SandboxCommandResult
        {
            ExecutionId = reply.ExecutionId,
            ExitCode = reply.ExitCode,
            StandardOutput = reply.StandardOutput,
            StandardError = reply.StandardError,
            Completed = reply.Completed,
            Duration = TimeSpan.FromMilliseconds(reply.DurationMs)
        };
    }

    private static int ToTimeoutSeconds(TimeSpan? timeout)
    {
        if (timeout is null || timeout.Value <= TimeSpan.Zero)
        {
            return 0;
        }

        return (int)Math.Min(Math.Ceiling(timeout.Value.TotalSeconds), int.MaxValue);
    }

    private static bool IsHandleInvalid(StatusCode statusCode)
    {
        // HostAgent maps a missing/killed/mismatched sandbox to NotFound or FailedPrecondition; both mean the handle is
        // no longer valid, which the SPI and its callers expect as SandboxHandleInvalidException.
        return statusCode is StatusCode.NotFound or StatusCode.FailedPrecondition;
    }

    private static SandboxHandleInvalidException ToHandleInvalid(RpcException exception)
    {
        return new SandboxHandleInvalidException(exception.Status.Detail, exception);
    }
}
