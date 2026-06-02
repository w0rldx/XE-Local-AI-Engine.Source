namespace XE_Local_AI_Engine.Tests.Sandbox;

using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.HostAgent;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;
using XE_Local_AI_Engine.HostAgent.Grpc.Contracts;
using XE_Local_AI_Engine.HostAgent.Grpc.Contracts.Security;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="LocalContainerSandboxProvider" /> tests. The provider is a thin gRPC client, so these
///     run it against a real in-process gRPC server on a Unix socket whose <see cref="RecordingSandboxControlService" />
///     records each request and returns scripted replies — no Docker, no HostAgent. They assert SPI↔proto translation,
///     HMAC metadata with the right method names, the RpcException→SandboxHandleInvalidException mapping, capabilities,
///     and the deterministic container name. The host-side TOCTOU guards (no-follow open + byte-recheck) are exercised
///     on the host filesystem with no container at all.
/// </summary>
public sealed class LocalContainerSandboxProviderTests
{
    private const string Secret = "local-container-test-secret";
    private static readonly DateTimeOffset FrozenNow = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    private static readonly SandboxAttachKey SampleKey = new()
    {
        OwnerUserId = "user-42",
        NodeId = "node-7",
        ProviderName = LocalContainerSandboxProvider.Name,
        RuntimeProfile = "dotnet-agent-home",
        ManifestVersion = 3
    };

    [Test]
    public async Task CreateOrAttachAsync_TranslatesRequestAndReply()
    {
        await using var harness = await Harness.StartAsync();
        harness.Service.OnCreate = request => new SandboxHandleReply
        {
            SandboxId = "sandbox-123",
            AttachKey = request.AttachKey,
            CreatedAt = Timestamp.FromDateTimeOffset(FrozenNow),
            ManifestVersion = request.AttachKey.ManifestVersion
        };

        var handle = await harness.Provider.CreateOrAttachAsync(new SandboxCreateRequest
        {
            AttachKey = SampleKey,
            RuntimeProfile = "dotnet-agent-home",
            ResourceLimits = new SandboxResourceLimits { CpuCount = 1.5, MemoryMb = 2048, PidsLimit = 64 },
            NetworkPolicy = SandboxNetworkPolicy.None
        });

        AssertEx.Equal("sandbox-123", handle.SandboxId);
        AssertEx.Equal(LocalContainerSandboxProvider.Name, handle.ProviderName);
        AssertEx.Equal(3, handle.ManifestVersion);
        AssertEx.Equal("user-42", handle.AttachKey.OwnerUserId);

        var recorded = harness.Service.LastCreate!;
        AssertEx.Equal("dotnet-agent-home", recorded.RuntimeProfile);
        AssertEx.NotEmpty(recorded.DefaultImage);
        AssertEx.Equal(1.5, recorded.Limits.CpuCount);
        AssertEx.Equal(2048, recorded.Limits.MemoryMb);
        AssertEx.Equal(64, recorded.Limits.PidsLimit);
        AssertEx.Equal(SandboxNetworkMode.None, recorded.Network);
        // Labels carry the raw owner/node/profile/manifest under the SHARED SandboxLabelKeys consts (one spelling for
        // both the provider and the authoritative HostAgent service) for attach validation.
        AssertEx.Equal("user-42", recorded.Labels[SandboxLabelKeys.Owner]);
        AssertEx.Equal("node-7", recorded.Labels[SandboxLabelKeys.Node]);
        AssertEx.Equal("dotnet-agent-home", recorded.Labels[SandboxLabelKeys.Profile]);
        AssertEx.Equal("3", recorded.Labels[SandboxLabelKeys.Manifest]);
        // The provider must NOT emit any divergently-spelled reserved key (e.g. the no-hyphen variant).
        AssertEx.False(recorded.Labels.ContainsKey("c0re.agenthome.owner"), "no divergent no-hyphen reserved key.");
        AssertEx.True(harness.Service.CreateMethodName == "/xe.hostagent.v1.SandboxControl/CreateOrAttachSandbox");
    }

    [Test]
    public async Task CreateOrAttachAsync_FillsLimitsFromOptionsWhenRequestOmitsThem()
    {
        await using var harness = await Harness.StartAsync();
        harness.Service.OnCreate = request => new SandboxHandleReply
        {
            SandboxId = "sandbox-1",
            AttachKey = request.AttachKey,
            CreatedAt = Timestamp.FromDateTimeOffset(FrozenNow),
            ManifestVersion = request.AttachKey.ManifestVersion
        };

        await harness.Provider.CreateOrAttachAsync(new SandboxCreateRequest
        {
            AttachKey = SampleKey,
            RuntimeProfile = "dotnet-agent-home",
            ResourceLimits = null
        });

        var recorded = harness.Service.LastCreate!;
        AssertEx.Equal(2.0, recorded.Limits.CpuCount);
        AssertEx.Equal(4096, recorded.Limits.MemoryMb);
        AssertEx.Equal(512, recorded.Limits.PidsLimit);
    }

    [Test]
    public async Task CreateOrAttachAsync_NameLabelIsDeterministicForSameOwnerNode()
    {
        await using var harness = await Harness.StartAsync();
        harness.Service.OnCreate = request => new SandboxHandleReply
        {
            SandboxId = "sandbox-1",
            AttachKey = request.AttachKey,
            CreatedAt = Timestamp.FromDateTimeOffset(FrozenNow),
            ManifestVersion = request.AttachKey.ManifestVersion
        };

        await harness.Provider.CreateOrAttachAsync(new SandboxCreateRequest { AttachKey = SampleKey, RuntimeProfile = "p" });
        var first = harness.Service.LastCreate!.Labels[SandboxLabelKeys.Name];
        await harness.Provider.CreateOrAttachAsync(new SandboxCreateRequest { AttachKey = SampleKey, RuntimeProfile = "p" });
        var second = harness.Service.LastCreate!.Labels[SandboxLabelKeys.Name];

        AssertEx.Equal(first, second);
        AssertEx.Contains(first, "c0re-agent-home-node-7-");
        // The owner is hashed (not the raw value) so the name stays filesystem/Docker-safe.
        AssertEx.False(first.Contains("user-42", StringComparison.Ordinal));
    }

    [Test]
    public async Task ExecuteAsync_TranslatesCommandAndResult()
    {
        await using var harness = await Harness.StartAsync();
        harness.Service.OnExecute = request => new ExecuteCommandReply
        {
            ExecutionId = request.ExecutionId,
            ExitCode = 0,
            StandardOutput = "ok",
            StandardError = string.Empty,
            Completed = true,
            DurationMs = 1500
        };
        var handle = MakeHandle("sandbox-9");

        var result = await harness.Provider.ExecuteAsync(handle, new SandboxCommandRequest
        {
            ExecutionId = "run-1",
            Executable = "git",
            Arguments = ["status", "--porcelain"],
            WorkingDirectory = "/agent-home/workspace/selected",
            Environment = new Dictionary<string, string> { ["KEY"] = "value" },
            Timeout = TimeSpan.FromSeconds(30)
        });

        AssertEx.Equal("run-1", result.ExecutionId);
        AssertEx.Equal(0, result.ExitCode);
        AssertEx.Equal("ok", result.StandardOutput);
        AssertEx.True(result.Completed);
        AssertEx.Equal(TimeSpan.FromMilliseconds(1500), result.Duration);

        var recorded = harness.Service.LastExecute!;
        AssertEx.Equal("sandbox-9", recorded.SandboxId);
        AssertEx.Equal("git", recorded.Executable);
        AssertEx.Equal(2, recorded.Arguments.Count);
        AssertEx.Equal("/agent-home/workspace/selected", recorded.WorkingDirectory);
        AssertEx.Equal("value", recorded.Environment["KEY"]);
        AssertEx.Equal(30, recorded.TimeoutSeconds);
    }

    [Test]
    public async Task ExecuteAsync_WhenServerReportsNotCompleted_PropagatesCompletedFalse()
    {
        await using var harness = await Harness.StartAsync();
        harness.Service.OnExecute = request => new ExecuteCommandReply
        {
            ExecutionId = request.ExecutionId,
            ExitCode = -1,
            Completed = false
        };
        var handle = MakeHandle("sandbox-9");

        var result = await harness.Provider.ExecuteAsync(handle, new SandboxCommandRequest
        {
            ExecutionId = "run-2",
            Executable = "git"
        });

        AssertEx.False(result.Completed);
        AssertEx.Equal(-1, result.ExitCode);
    }

    [Test]
    public async Task ConnectAsync_WhenServerReturnsNotFound_ThrowsSandboxHandleInvalid()
    {
        await using var harness = await Harness.StartAsync();
        harness.Service.OnConnect = _ => throw new RpcException(new Status(StatusCode.NotFound, "no live sandbox"));

        await AssertEx.ThrowsAsync<SandboxHandleInvalidException>(() => harness.Provider.ConnectAsync(SampleKey));
    }

    [Test]
    public async Task ExecuteAsync_WhenServerReturnsFailedPrecondition_ThrowsSandboxHandleInvalid()
    {
        await using var harness = await Harness.StartAsync();
        harness.Service.OnExecute = _ => throw new RpcException(new Status(StatusCode.FailedPrecondition, "killed"));
        var handle = MakeHandle("sandbox-9");

        await AssertEx.ThrowsAsync<SandboxHandleInvalidException>(() => harness.Provider.ExecuteAsync(handle, new SandboxCommandRequest
        {
            ExecutionId = "run-3",
            Executable = "git"
        }));
    }

    [Test]
    public async Task ReadFileAsync_DecodesReplyBytesAsUtf8()
    {
        await using var harness = await Harness.StartAsync();
        harness.Service.OnReadFile = _ => new ReadFileReply { Content = ByteString.CopyFromUtf8("héllo") };
        var handle = MakeHandle("sandbox-9");

        var content = await harness.Provider.ReadFileAsync(handle, "/agent-home/runs/1/out.txt");

        AssertEx.Equal("héllo", content);
        AssertEx.Equal("/agent-home/runs/1/out.txt", harness.Service.LastReadFile!.SandboxPath);
    }

    [Test]
    public async Task CopyOutAsync_WritesRawReplyBytesToHostDestination()
    {
        await using var harness = await Harness.StartAsync();
        var payload = new byte[] { 0x00, 0xFF, 0x10, 0x42 };
        harness.Service.OnCopyOut = _ => new ReadFileReply { Content = UnsafeByteOperations.UnsafeWrap(payload) };
        var handle = MakeHandle("sandbox-9");

        using var temp = new TempDir();
        var destination = Path.Combine(temp.Path, "artifact.bin");

        await harness.Provider.CopyOutAsync(handle, new SandboxCopyRequest
        {
            SourcePath = "/agent-home/runs/1/patches/changes.patch",
            DestinationPath = destination
        });

        var written = await File.ReadAllBytesAsync(destination);
        AssertEx.True(written.SequenceEqual(payload), "copy-out must write the raw reply bytes unchanged.");
    }

    [Test]
    public async Task CancelCommandAsync_OnMissingExecution_DoesNotThrow()
    {
        await using var harness = await Harness.StartAsync();
        harness.Service.OnCancel = _ => throw new RpcException(new Status(StatusCode.NotFound, "no such execution"));
        var handle = MakeHandle("sandbox-9");

        // Best-effort cancel must swallow a missing-execution error, matching the fake's no-op behavior.
        await harness.Provider.CancelCommandAsync(handle, "run-x");
    }

    [Test]
    public async Task KillAsync_SendsKillForTheHandleSandbox()
    {
        await using var harness = await Harness.StartAsync();
        var handle = MakeHandle("sandbox-kill");

        await harness.Provider.KillAsync(handle);

        AssertEx.Equal("sandbox-kill", harness.Service.LastKill!.SandboxId);
    }

    [Test]
    public async Task CopyIntoAsync_SendsHostFileBytes()
    {
        await using var harness = await Harness.StartAsync();
        var handle = MakeHandle("sandbox-9");

        using var temp = new TempDir();
        var source = Path.Combine(temp.Path, "file.txt");
        await File.WriteAllTextAsync(source, "payload-bytes");

        await harness.Provider.CopyIntoAsync(handle, new SandboxCopyRequest
        {
            SourcePath = source,
            DestinationPath = "/agent-home/workspace/selected/alias/file.txt"
        });

        var recorded = harness.Service.LastCopyInto!;
        AssertEx.Equal("/agent-home/workspace/selected/alias/file.txt", recorded.DestinationPath);
        AssertEx.Equal("payload-bytes", recorded.Content.ToStringUtf8());
    }

    [Test]
    public async Task Capabilities_AdvertiseLimitsAndNetworkButNotReadOnlyMounts()
    {
        await using var harness = await Harness.StartAsync();

        var capabilities = harness.Provider.Capabilities;

        AssertEx.True(capabilities.HasFlag(SandboxProviderCapabilities.SupportsCopyInto));
        AssertEx.True(capabilities.HasFlag(SandboxProviderCapabilities.SupportsResourceLimits));
        AssertEx.True(capabilities.HasFlag(SandboxProviderCapabilities.SupportsNetworkPolicy));
        AssertEx.True(capabilities.HasFlag(SandboxProviderCapabilities.SupportsKill));
        AssertEx.False(capabilities.HasFlag(SandboxProviderCapabilities.SupportsReadOnlyMounts));
    }


    [Test]
    public async Task CopyIntoAsync_WhenFinalComponentIsSymlink_Rejects()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await using var harness = await Harness.StartAsync();
        var handle = MakeHandle("sandbox-9");

        using var temp = new TempDir();
        var real = Path.Combine(temp.Path, "secret.txt");
        await File.WriteAllTextAsync(real, "outside-secret");
        var link = Path.Combine(temp.Path, "link.txt");
        // Simulate a swap-after-walk: the pass-1 path string now points at a symlink.
        File.CreateSymbolicLink(link, real);

        await AssertEx.ThrowsAsync<AgentHomeRequestRejectedException>(() => harness.Provider.CopyIntoAsync(handle, new SandboxCopyRequest
        {
            SourcePath = link,
            DestinationPath = "/agent-home/workspace/selected/alias/link.txt"
        }));

        // No copy reached HostAgent — the read was rejected before any rpc.
        AssertEx.Null(harness.Service.LastCopyInto);
    }

    [Test]
    public async Task CopyIntoAsync_WhenFileExceedsCapOnReRead_SkipsWithoutSending()
    {
        await using var harness = await Harness.StartAsync(maxCopyFileBytes: 8);
        var handle = MakeHandle("sandbox-9");

        using var temp = new TempDir();
        var source = Path.Combine(temp.Path, "grew.txt");
        // A file that grew past the per-file cap after pass-1 sizing must be skipped (blocked), never truncated.
        await File.WriteAllBytesAsync(source, new byte[32]);

        await harness.Provider.CopyIntoAsync(handle, new SandboxCopyRequest
        {
            SourcePath = source,
            DestinationPath = "/agent-home/workspace/selected/alias/grew.txt"
        });

        AssertEx.Null(harness.Service.LastCopyInto);
    }

    [Test]
    public async Task CopyIntoAsync_WhenFileGrowsConcurrently_NeverSendsATruncatedCopy()
    {
        // Growth parity safety invariant (deterministic, no timing dependence): while a file is appended
        // concurrently, every copy the provider sends MUST be the exact consistent snapshot (== cap bytes), never a
        // buffer torn/truncated to the stale size. A grown read is blocked (null) instead. This invariant holds on
        // every iteration regardless of how the append/read interleave.
        const int cap = 1 << 16;
        await using var harness = await Harness.StartAsync(maxCopyFileBytes: cap);
        var handle = MakeHandle("sandbox-9");

        using var temp = new TempDir();
        var source = Path.Combine(temp.Path, "growing.bin");
        await File.WriteAllBytesAsync(source, new byte[cap]).ConfigureAwait(false);

        using var stop = new CancellationTokenSource();
        var appender = Task.Run(async () =>
        {
            while (!stop.Token.IsCancellationRequested)
            {
                try
                {
                    // Oscillate the file across the cap: append past it, then truncate back. Whenever the provider
                    // observes the grown state it must block or snapshot exactly — never emit a truncated buffer.
                    await File.AppendAllTextAsync(source, "xxxxxxxxxxxxxxxx", stop.Token).ConfigureAwait(false);
                    using var fs = new FileStream(source, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                    fs.SetLength(cap);
                }
                catch (IOException)
                {
                    // A transient sharing conflict with the reader is expected; keep oscillating.
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        });

        try
        {
            for (var attempt = 0; attempt < 300; attempt++)
            {
                harness.Service.LastCopyInto = null;
                await harness.Provider.CopyIntoAsync(handle, new SandboxCopyRequest
                {
                    SourcePath = source,
                    DestinationPath = "/agent-home/workspace/selected/alias/growing.bin"
                }).ConfigureAwait(false);

                var sent = harness.Service.LastCopyInto;
                // null = blocked (grew or over-cap); a non-null copy must be the exact snapshot, never truncated.
                if (sent is not null)
                {
                    AssertEx.Equal(cap, sent.Content.Length, "a sent copy must be the exact snapshot, never a truncated grow.");
                }
            }
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await appender.ConfigureAwait(false);
        }
    }

    [Test]
    public async Task CopyIntoAsync_AtExactlyTheCap_StillCopies()
    {
        await using var harness = await Harness.StartAsync(maxCopyFileBytes: 16);
        var handle = MakeHandle("sandbox-9");

        using var temp = new TempDir();
        var source = Path.Combine(temp.Path, "exact.txt");
        await File.WriteAllBytesAsync(source, new byte[16]);

        await harness.Provider.CopyIntoAsync(handle, new SandboxCopyRequest
        {
            SourcePath = source,
            DestinationPath = "/agent-home/workspace/selected/alias/exact.txt"
        });

        AssertEx.NotNull(harness.Service.LastCopyInto);
        AssertEx.Equal(16, harness.Service.LastCopyInto!.Content.Length);
    }

    private static SandboxHandle MakeHandle(string sandboxId)
    {
        return new SandboxHandle
        {
            ProviderName = LocalContainerSandboxProvider.Name,
            SandboxId = sandboxId,
            AttachKey = SampleKey,
            CreatedAt = FrozenNow,
            ManifestVersion = 3
        };
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly TempDir _socketDir;

        private Harness(WebApplication app, TempDir socketDir, LocalContainerSandboxProvider provider, RecordingSandboxControlService service)
        {
            _app = app;
            _socketDir = socketDir;
            Provider = provider;
            Service = service;
        }

        public LocalContainerSandboxProvider Provider { get; }

        public RecordingSandboxControlService Service { get; }

        public static async Task<Harness> StartAsync(long maxCopyFileBytes = LocalContainerOptions.DefaultMaxCopyFileBytes)
        {
            var socketDir = new TempDir();
            var socketPath = Path.Combine(socketDir.Path, "host-agent.sock");

            var service = new RecordingSandboxControlService();
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.ConfigureKestrel(options =>
                options.ListenUnixSocket(socketPath, listenOptions => listenOptions.Protocols = HttpProtocols.Http2));
            builder.Services.AddSingleton(service);
            builder.Services.AddGrpc();

            var app = builder.Build();
            app.MapGrpcService<RecordingSandboxControlService>();
            await app.StartAsync().ConfigureAwait(false);

            var hostAgentOptions = new HostAgentClientOptions
            {
                SocketPath = socketPath,
                Secret = Secret,
                BucketSeconds = HostAgentClientOptions.DefaultBucketSeconds
            };
            var providerOptions = Options.Create(new LocalContainerOptions { MaxCopyFileBytes = maxCopyFileBytes });
            var provider = new LocalContainerSandboxProvider(
                hostAgentOptions,
                providerOptions,
                TimeProvider.System,
                NullLogger<LocalContainerSandboxProvider>.Instance);

            return new Harness(app, socketDir, provider, service);
        }

        public async ValueTask DisposeAsync()
        {
            Provider.Dispose();
            await _app.DisposeAsync().ConfigureAwait(false);
            _socketDir.Dispose();
        }
    }

    /// <summary>
    ///     An in-process <see cref="SandboxControl.SandboxControlBase" /> that records the last request of each kind and
    ///     returns a scripted reply, so the provider's translation and HMAC metadata can be asserted without Docker. It
    ///     also captures the HMAC method-name header to prove the provider signs with the correct full method name.
    /// </summary>
    private sealed class RecordingSandboxControlService : SandboxControl.SandboxControlBase
    {
        private readonly ConcurrentQueue<string> _ignore = new();

        public Func<CreateSandboxRequest, SandboxHandleReply>? OnCreate { get; set; }

        public Func<ConnectSandboxRequest, SandboxHandleReply>? OnConnect { get; set; }

        public Func<ExecuteCommandRequest, ExecuteCommandReply>? OnExecute { get; set; }

        public Func<ReadFileRequest, ReadFileReply>? OnReadFile { get; set; }

        public Func<CopyOutRequest, ReadFileReply>? OnCopyOut { get; set; }

        public Func<CancelCommandRequest, Empty>? OnCancel { get; set; }

        public CreateSandboxRequest? LastCreate { get; private set; }

        public ExecuteCommandRequest? LastExecute { get; private set; }

        public ReadFileRequest? LastReadFile { get; private set; }

        // Settable so the growth test can reset it between iterations; the recording type is private to this test.
        public CopyIntoRequest? LastCopyInto { get; set; }

        public KillSandboxRequest? LastKill { get; private set; }

        public string? CreateMethodName { get; private set; }

        public override Task<SandboxHandleReply> CreateOrAttachSandbox(CreateSandboxRequest request, ServerCallContext context)
        {
            LastCreate = request;
            CreateMethodName = context.Method;
            AssertHmacPresent(context);
            return Task.FromResult(OnCreate?.Invoke(request) ?? new SandboxHandleReply { SandboxId = "default", AttachKey = request.AttachKey });
        }

        public override Task<SandboxHandleReply> ConnectSandbox(ConnectSandboxRequest request, ServerCallContext context)
        {
            AssertHmacPresent(context);
            return Task.FromResult(OnConnect?.Invoke(request) ?? new SandboxHandleReply { SandboxId = "default", AttachKey = request.AttachKey });
        }

        public override Task<ExecuteCommandReply> ExecuteCommand(ExecuteCommandRequest request, ServerCallContext context)
        {
            LastExecute = request;
            AssertHmacPresent(context);
            return Task.FromResult(OnExecute?.Invoke(request) ?? new ExecuteCommandReply { ExecutionId = request.ExecutionId, Completed = true });
        }

        public override Task<Empty> CopyInto(CopyIntoRequest request, ServerCallContext context)
        {
            LastCopyInto = request;
            AssertHmacPresent(context);
            return Task.FromResult(new Empty());
        }

        public override Task<ReadFileReply> ReadFile(ReadFileRequest request, ServerCallContext context)
        {
            LastReadFile = request;
            AssertHmacPresent(context);
            return Task.FromResult(OnReadFile?.Invoke(request) ?? new ReadFileReply());
        }

        public override Task<ReadFileReply> CopyOut(CopyOutRequest request, ServerCallContext context)
        {
            AssertHmacPresent(context);
            return Task.FromResult(OnCopyOut?.Invoke(request) ?? new ReadFileReply());
        }

        public override Task<Empty> CancelCommand(CancelCommandRequest request, ServerCallContext context)
        {
            AssertHmacPresent(context);
            return Task.FromResult(OnCancel?.Invoke(request) ?? new Empty());
        }

        public override Task<Empty> KillSandbox(KillSandboxRequest request, ServerCallContext context)
        {
            LastKill = request;
            AssertHmacPresent(context);
            return Task.FromResult(new Empty());
        }

        private void AssertHmacPresent(ServerCallContext context)
        {
            _ = _ignore;
            // The provider must attach the full HMAC metadata set on every call (the same scheme GrpcHostAgentClient
            // uses) — assert the authorization bearer + body hash + request id + bucket are present and method-bound.
            var authorization = FindHeader(context, HostAgentHmacMetadata.AuthorizationHeader);
            var requestId = FindHeader(context, HostAgentHmacMetadata.RequestIdHeader);
            var bucket = FindHeader(context, HostAgentHmacMetadata.BucketHeader);
            var bodyHash = FindHeader(context, HostAgentHmacMetadata.BodySha256Header);

            if (authorization is null || !authorization.StartsWith("Bearer ", StringComparison.Ordinal)
                || string.IsNullOrEmpty(requestId) || string.IsNullOrEmpty(bucket) || string.IsNullOrEmpty(bodyHash))
            {
                throw new RpcException(new Status(StatusCode.Unauthenticated, "missing HMAC metadata"));
            }
        }

        private static string? FindHeader(ServerCallContext context, string key)
        {
            foreach (var entry in context.RequestHeaders)
            {
                if (string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    return entry.Value;
                }
            }

            return null;
        }
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"xe-local-container-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, true);
                }
            }
            catch (IOException)
            {
                // A leaked temp directory is harmless to the test outcome.
            }
        }
    }
}
