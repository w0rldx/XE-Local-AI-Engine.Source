namespace XE_Local_AI_Engine.Tests.HostAgent;

using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using XE_Local_AI_Engine.HostAgent.Grpc.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Build-barrier coverage for the HostAgent <c>SandboxControl</c> proto surface (kept in
///     <c>HostAgent.Grpc.Contracts</c>): the unary-only RPC set and lossless serialization of the messages (bytes for
///     copy/read, the network enum, maps, the nested attach key, and the handle timestamp). The former
///     <c>LocalContainerSandboxProvider</c> that consumed this proto was removed in Lane D; the proto contract is
///     retained and still build-barrier-tested here.
/// </summary>
public sealed class SandboxControlProtoTests
{
    private static readonly string[] ExpectedRpcNames =
    [
        "CreateOrAttachSandbox",
        "ConnectSandbox",
        "ExecuteCommand",
        "CopyInto",
        "ReadFile",
        "CopyOut",
        "CancelCommand",
        "KillSandbox"
    ];

    [Test]
    public void SandboxControl_DeclaresExactlyTheExpectedRpcs()
    {
        var actual = SandboxControl.Descriptor.Methods.Select(method => method.Name).ToList();

        AssertEx.Equal(ExpectedRpcNames.Length, actual.Count);
        foreach (var expected in ExpectedRpcNames)
        {
            AssertEx.Contains(actual, expected);
        }
    }

    // The global HMAC interceptor only overrides the unary + server-streaming handlers, so a client- or
    // bidi-streaming rpc on SandboxControl would silently bypass HMAC authentication. This guard
    // fails the build if any SandboxControl rpc is ever made streaming.
    [Test]
    public void SandboxControl_EveryRpcIsUnary()
    {
        foreach (var method in SandboxControl.Descriptor.Methods)
        {
            AssertEx.False(method.IsClientStreaming, $"SandboxControl.{method.Name} must not be client-streaming (would bypass HMAC).");
            AssertEx.False(method.IsServerStreaming, $"SandboxControl.{method.Name} must not be server-streaming (would bypass HMAC).");
        }
    }

    [Test]
    public void SandboxAttachKeyMessage_RoundTripsAllFields()
    {
        var original = new SandboxAttachKeyMessage
        {
            OwnerUserId = "owner-1",
            NodeId = "node-7",
            ProviderName = "local-container",
            RuntimeProfile = "dotnet-agent-home",
            ManifestVersion = 3
        };

        var roundTripped = RoundTrip(original, SandboxAttachKeyMessage.Parser);

        AssertEx.Equal(original, roundTripped);
    }

    [Test]
    public void CreateSandboxRequest_RoundTripsLimitsNetworkEnumAndLabels()
    {
        var original = new CreateSandboxRequest
        {
            AttachKey = new SandboxAttachKeyMessage
            {
                OwnerUserId = "owner-1",
                NodeId = "node-7",
                ProviderName = "local-container",
                RuntimeProfile = "dotnet-agent-home",
                ManifestVersion = 5
            },
            RuntimeProfile = "dotnet-agent-home",
            DefaultImage = "dotnet-agent-home:2026-05-agenthome-mvp",
            Limits = new ResourceLimitsMessage
            {
                CpuCount = 2.0,
                MemoryMb = 4096,
                PidsLimit = 512
            },
            Network = SandboxNetworkMode.Restricted,
            Labels =
            {
                ["owner"] = "owner-1",
                ["node"] = "node-7"
            }
        };

        var roundTripped = RoundTrip(original, CreateSandboxRequest.Parser);

        AssertEx.Equal(original, roundTripped);
        AssertEx.Equal(SandboxNetworkMode.Restricted, roundTripped.Network);
        AssertEx.Equal(2.0, roundTripped.Limits.CpuCount);
        AssertEx.Equal("owner-1", roundTripped.Labels["owner"]);
    }

    [Test]
    public void SandboxNetworkMode_DefaultIsNone()
    {
        // Mirrors SandboxNetworkPolicy.None == 0 — the secure default when no network is requested.
        var request = new CreateSandboxRequest();

        AssertEx.Equal(SandboxNetworkMode.None, request.Network);
    }

    [Test]
    public void SandboxHandleReply_RoundTripsTimestampAndAttachKey()
    {
        var createdAt = new DateTimeOffset(2026, 5, 30, 8, 0, 0, TimeSpan.Zero);
        var original = new SandboxHandleReply
        {
            SandboxId = "c0re-agent-home-node-7-abc123",
            AttachKey = new SandboxAttachKeyMessage
            {
                OwnerUserId = "owner-1",
                NodeId = "node-7",
                ProviderName = "local-container",
                RuntimeProfile = "dotnet-agent-home",
                ManifestVersion = 5
            },
            CreatedAt = Timestamp.FromDateTimeOffset(createdAt),
            ManifestVersion = 5
        };

        var roundTripped = RoundTrip(original, SandboxHandleReply.Parser);

        AssertEx.Equal(original, roundTripped);
        AssertEx.Equal(createdAt, roundTripped.CreatedAt.ToDateTimeOffset());
    }

    [Test]
    public void ExecuteCommandRequest_RoundTripsArgumentsEnvironmentAndStdinBytes()
    {
        var stdin = new byte[]
        {
            0x00,
            0x01,
            0x7f,
            0xff,
            0x2a
        };
        var original = new ExecuteCommandRequest
        {
            SandboxId = "sandbox-1",
            ExecutionId = "exec-1",
            Executable = "git",
            Arguments =
            {
                "diff",
                "--binary"
            },
            WorkingDirectory = "/agent-home/workspace/selected",
            Environment =
            {
                ["GIT_TERMINAL_PROMPT"] = "0"
            },
            TimeoutSeconds = 30,
            StandardInput = ByteString.CopyFrom(stdin)
        };

        var roundTripped = RoundTrip(original, ExecuteCommandRequest.Parser);

        AssertEx.Equal(original, roundTripped);
        AssertEx.Contains(roundTripped.Arguments, "--binary");
        AssertEx.Equal("0", roundTripped.Environment["GIT_TERMINAL_PROMPT"]);
        AssertEx.True(stdin.AsSpan().SequenceEqual(roundTripped.StandardInput.Span), "stdin bytes must round-trip unchanged.");
    }

    [Test]
    public void ExecuteCommandReply_RoundTripsExitCodeAndDuration()
    {
        var original = new ExecuteCommandReply
        {
            ExecutionId = "exec-1",
            ExitCode = -1,
            StandardOutput = "diff output",
            StandardError = "warning: redacted",
            Completed = false,
            DurationMs = 1234
        };

        var roundTripped = RoundTrip(original, ExecuteCommandReply.Parser);

        AssertEx.Equal(original, roundTripped);
        AssertEx.Equal(-1, roundTripped.ExitCode);
        AssertEx.False(roundTripped.Completed);
    }

    [Test]
    public void CopyIntoRequest_RoundTripsContentBytesAndFileMode()
    {
        // 0xff is non-UTF8; this proves the wire transport is binary-safe bytes, not strings.
        var content = new byte[]
        {
            0xde,
            0xad,
            0xbe,
            0xef,
            0x00
        };
        var original = new CopyIntoRequest
        {
            SandboxId = "sandbox-1",
            DestinationPath = "/agent-home/workspace/selected/app/bin/image.png",
            Content = ByteString.CopyFrom(content),
            FileMode = 0b110_100_100
        };

        var roundTripped = RoundTrip(original, CopyIntoRequest.Parser);

        AssertEx.Equal(original, roundTripped);
        AssertEx.True(content.AsSpan().SequenceEqual(roundTripped.Content.Span), "content bytes must round-trip unchanged.");
        AssertEx.Equal(0b110_100_100u, roundTripped.FileMode);
    }

    [Test]
    public void ReadFileReply_RoundTripsRawBytes()
    {
        var content = new byte[]
        {
            0x00,
            0xff,
            0x10,
            0x80
        };
        var original = new ReadFileReply
        {
            Content = ByteString.CopyFrom(content)
        };

        var roundTripped = RoundTrip(original, ReadFileReply.Parser);

        AssertEx.True(content.AsSpan().SequenceEqual(roundTripped.Content.Span), "read-file bytes must round-trip unchanged.");
    }

    [Test]
    public void ReadFileRequest_CopyOutRequest_CancelCommandRequest_KillSandboxRequest_RoundTrip()
    {
        var read = RoundTrip(new ReadFileRequest
        {
            SandboxId = "s1",
            SandboxPath = "/agent-home/out.txt"
        }, ReadFileRequest.Parser);
        var copyOut = RoundTrip(new CopyOutRequest
        {
            SandboxId = "s1",
            SourcePath = "/agent-home/out.txt"
        }, CopyOutRequest.Parser);
        var cancel = RoundTrip(new CancelCommandRequest
        {
            SandboxId = "s1",
            ExecutionId = "exec-1"
        }, CancelCommandRequest.Parser);
        var kill = RoundTrip(new KillSandboxRequest
        {
            SandboxId = "s1"
        }, KillSandboxRequest.Parser);
        var connect = RoundTrip(new ConnectSandboxRequest
            {
                AttachKey = new SandboxAttachKeyMessage
                {
                    OwnerUserId = "owner-1",
                    NodeId = "node-7",
                    ProviderName = "local-container",
                    RuntimeProfile = "dotnet-agent-home",
                    ManifestVersion = 1
                }
            },
            ConnectSandboxRequest.Parser);

        AssertEx.Equal("/agent-home/out.txt", read.SandboxPath);
        AssertEx.Equal("/agent-home/out.txt", copyOut.SourcePath);
        AssertEx.Equal("exec-1", cancel.ExecutionId);
        AssertEx.Equal("s1", kill.SandboxId);
        AssertEx.Equal("owner-1", connect.AttachKey.OwnerUserId);
    }

    private static T RoundTrip<T>(T message, MessageParser<T> parser) where T : IMessage<T>
    {
        return parser.ParseFrom(message.ToByteArray());
    }
}
