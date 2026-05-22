namespace XE_Local_AI_Engine.Tests.HostAgent;

using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.HostAgent.Grpc.Contracts.Security;
using XE_Local_AI_Engine.HostAgent.Linux.Security;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class HostAgentHmacMetadataTests
{
    private const string Secret = "test-secret";
    private const string MethodName = "/xe.hostagent.v1.HostAgentControl/GetStatus";
    private static readonly DateTimeOffset FrozenNow = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    [Test]
    public void Create_WhenUsedByClient_IsAcceptedByLinuxValidator()
    {
        var validator = CreateValidator();
        var request = new Empty();
        var headers = HostAgentHmacMetadata.Create(request,
            MethodName,
            Secret,
            new FrozenTimeProvider(FrozenNow),
            HostAgentHmacOptions.DefaultBucketSeconds);

        var result = validator.Validate(request, headers, MethodName);

        AssertEx.True(result.Succeeded);
    }

    [Test]
    public async Task Create_WhenSecretIsBlank_ThrowsArgumentException()
    {
        await AssertEx.ThrowsAsync<ArgumentException>(() => Task.Run(() =>
            HostAgentHmacMetadata.Create(new Empty(),
                MethodName,
                "   ",
                new FrozenTimeProvider(FrozenNow),
                HostAgentHmacOptions.DefaultBucketSeconds)));
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
                HostAgentHmacOptions.DefaultBucketSeconds)));
    }

    private static HmacRequestValidator CreateValidator()
    {
        var options = new TestOptionsMonitor<HostAgentHmacOptions>(new HostAgentHmacOptions
        {
            Secret = Secret,
            BucketSeconds = HostAgentHmacOptions.DefaultBucketSeconds,
            MaxRequestIdsPerBucket = HostAgentHmacOptions.DefaultMaxRequestIdsPerBucket
        });

        return new HmacRequestValidator(options, new ReplayWindowCache(), new FrozenTimeProvider(FrozenNow));
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

    private sealed class TestOptionsMonitor<TOptions> : IOptionsMonitor<TOptions>
    {
        public TestOptionsMonitor(TOptions value)
        {
            CurrentValue = value;
        }

        public TOptions CurrentValue { get; }

        public TOptions Get(string? name)
        {
            return CurrentValue;
        }

        public IDisposable? OnChange(Action<TOptions, string?> listener)
        {
            return null;
        }
    }
}
