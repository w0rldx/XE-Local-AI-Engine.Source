namespace XE_Local_AI_Engine.Tests.HostAgent;

using Google.Protobuf.WellKnownTypes;
using XE_Local_AI_Engine.Client.Services.HostAgent;
using XE_Local_AI_Engine.HostAgent.Grpc.Contracts.Security;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class HostAgentHmacMetadataTests
{
    private const string Secret = "test-secret";
    private const string MethodName = "/xe.hostagent.v1.HostAgentControl/GetStatus";
    private static readonly DateTimeOffset FrozenNow = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    // The client-side HMAC signer (HostAgentHmacMetadata, in the kept Grpc.Contracts) is the SUT here. The
    // server-side round-trip validation (formerly HmacRequestValidator in the deleted HostAgent.Linux) is no longer
    // exercised — the validator lived in the removed Docker/runtime daemon; the kept connection layer only signs.

    [Test]
    public async Task Create_WhenSecretIsBlank_ThrowsArgumentException()
    {
        await AssertEx.ThrowsAsync<ArgumentException>(() => Task.Run(() =>
            HostAgentHmacMetadata.Create(new Empty(),
                MethodName,
                "   ",
                new FrozenTimeProvider(FrozenNow),
                HostAgentClientOptions.DefaultBucketSeconds)));
    }

    [Test]
    public async Task Create_WhenBucketSecondsIsNonPositive_ThrowsArgumentOutOfRange()
    {
        await AssertEx.ThrowsAsync<ArgumentOutOfRangeException>(() => Task.Run(() =>
            HostAgentHmacMetadata.Create(new Empty(),
                MethodName,
                Secret,
                new FrozenTimeProvider(FrozenNow),
                0)));
    }

    [Test]
    public async Task Create_WhenRequestIsNull_ThrowsArgumentNull()
    {
        await AssertEx.ThrowsAsync<ArgumentNullException>(() => Task.Run(() =>
            HostAgentHmacMetadata.Create(null!,
                MethodName,
                Secret,
                new FrozenTimeProvider(FrozenNow),
                HostAgentClientOptions.DefaultBucketSeconds)));
    }

    private sealed class FrozenTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FrozenTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return _utcNow;
        }
    }
}
