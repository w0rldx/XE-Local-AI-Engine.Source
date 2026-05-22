namespace XE_Local_AI_Engine.Tests.HostAgent;

using System.Globalization;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.HostAgent.Grpc.Contracts;
using XE_Local_AI_Engine.HostAgent.Linux.Hosting;
using XE_Local_AI_Engine.HostAgent.Linux.Security;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class HostAgentLinuxHmacTests
{
    private const string Secret = "test-secret";
    private const string MethodName = "/xe.hostagent.v1.HostAgentControl/GetStatus";
    private static readonly DateTimeOffset FrozenNow = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    [Test]
    public void Validate_WhenBearerIsMissing_ReturnsUnauthenticated()
    {
        var validator = CreateValidator();
        var headers = CreateHeaders(new Empty(), "missing-bearer", includeBearer: false);

        var result = validator.Validate(new Empty(), headers, MethodName);

        AssertEx.False(result.Succeeded);
        AssertEx.Equal(StatusCode.Unauthenticated, result.StatusCode);
    }

    [Test]
    public void Validate_WhenBucketIsExpired_ReturnsUnauthenticated()
    {
        var validator = CreateValidator();
        var expiredBucket = CurrentBucket() - 1;
        var headers = CreateHeaders(new Empty(), "expired", expiredBucket);

        var result = validator.Validate(new Empty(), headers, MethodName);

        AssertEx.False(result.Succeeded);
        AssertEx.Equal(StatusCode.Unauthenticated, result.StatusCode);
    }

    [Test]
    public void Validate_WhenBodyHashIsTampered_ReturnsUnauthenticated()
    {
        var validator = CreateValidator();
        var request = new ContainerActionRequest
        {
            ContainerName = "ollama"
        };
        var headers = CreateHeaders(request, "tampered-body", bodySha256: new string('0', 64));

        var result = validator.Validate(request, headers, "/xe.hostagent.v1.HostAgentControl/StartContainer");

        AssertEx.False(result.Succeeded);
        AssertEx.Equal(StatusCode.Unauthenticated, result.StatusCode);
    }

    [Test]
    public void Validate_WhenRequestIdIsReplayed_ReturnsAlreadyExists()
    {
        var validator = CreateValidator();
        var request = new Empty();
        var headers = CreateHeaders(request, "replayed-request");

        var firstResult = validator.Validate(request, headers, MethodName);
        var secondResult = validator.Validate(request, headers, MethodName);

        AssertEx.True(firstResult.Succeeded);
        AssertEx.False(secondResult.Succeeded);
        AssertEx.Equal(StatusCode.AlreadyExists, secondResult.StatusCode);
    }

    [Test]
    public void DefaultSocketFileMode_IsUserAndGroupReadWriteOnly()
    {
        AssertEx.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite,
            HostAgentSocketOptions.DefaultSocketFileMode);
    }

    [Test]
    public void Validate_WhenBucketInFuture_ReturnsUnauthenticated()
    {
        var validator = CreateValidator();
        var futureBucket = CurrentBucket() + 1;
        var headers = CreateHeaders(new Empty(), "future-bucket", futureBucket);

        var result = validator.Validate(new Empty(), headers, MethodName);

        AssertEx.False(result.Succeeded);
        AssertEx.Equal(StatusCode.Unauthenticated, result.StatusCode);
    }

    [Test]
    public void Validate_WhenSameRequestIdInNewBucket_IsAccepted()
    {
        var timeProvider = new MutableTimeProvider(FrozenNow);
        var validator = CreateValidator(timeProvider);
        const string requestId = "rollover-request";

        var firstHeaders = CreateHeaders(new Empty(), requestId, CurrentBucket());
        var firstResult = validator.Validate(new Empty(), firstHeaders, MethodName);

        AssertEx.True(firstResult.Succeeded);

        // Advance the clock past exactly one bucket so the request id lands in a brand-new bucket.
        timeProvider.Advance(TimeSpan.FromSeconds(HostAgentHmacOptions.DefaultBucketSeconds));

        var newBucket = timeProvider.GetUtcNow().ToUnixTimeSeconds() / HostAgentHmacOptions.DefaultBucketSeconds;
        var secondHeaders = CreateHeaders(new Empty(), requestId, newBucket);
        var secondResult = validator.Validate(new Empty(), secondHeaders, MethodName);

        AssertEx.True(secondResult.Succeeded);
    }

    [Test]
    public void Validate_WhenSecretMissing_ReturnsUnauthenticated()
    {
        var options = new TestOptionsMonitor<HostAgentHmacOptions>(new HostAgentHmacOptions
        {
            Secret = string.Empty,
            BucketSeconds = HostAgentHmacOptions.DefaultBucketSeconds,
            MaxRequestIdsPerBucket = HostAgentHmacOptions.DefaultMaxRequestIdsPerBucket
        });
        var validator = new HmacRequestValidator(options, new ReplayWindowCache(), new FrozenTimeProvider(FrozenNow));
        var headers = CreateHeaders(new Empty(), "no-secret");

        var result = validator.Validate(new Empty(), headers, MethodName);

        AssertEx.False(result.Succeeded);
        AssertEx.Equal(StatusCode.Unauthenticated, result.StatusCode);
    }

    [Test]
    public void Validate_WhenBucketTextNonNumeric_ReturnsUnauthenticated()
    {
        var validator = CreateValidator();
        var request = new Empty();
        var resolvedBodySha256 = HmacRequestValidator.ComputeBodySha256(request);
        var hmac = HmacRequestValidator.ComputeHmac(Secret, MethodName, "non-numeric-bucket", resolvedBodySha256, CurrentBucket());

        var headers = new Metadata
        {
            {
                HmacRequestValidator.RequestIdHeader, "non-numeric-bucket"
            },
            {
                HmacRequestValidator.BucketHeader, "not-a-number"
            },
            {
                HmacRequestValidator.BodySha256Header, resolvedBodySha256
            },
            {
                HmacRequestValidator.AuthorizationHeader, $"Bearer {hmac}"
            }
        };

        var result = validator.Validate(request, headers, MethodName);

        AssertEx.False(result.Succeeded);
        AssertEx.Equal(StatusCode.Unauthenticated, result.StatusCode);
    }

    private static HmacRequestValidator CreateValidator(TimeProvider? timeProvider = null)
    {
        var options = new TestOptionsMonitor<HostAgentHmacOptions>(new HostAgentHmacOptions
        {
            Secret = Secret,
            BucketSeconds = HostAgentHmacOptions.DefaultBucketSeconds,
            MaxRequestIdsPerBucket = HostAgentHmacOptions.DefaultMaxRequestIdsPerBucket
        });

        return new HmacRequestValidator(options, new ReplayWindowCache(), timeProvider ?? new FrozenTimeProvider(FrozenNow));
    }

    private static Metadata CreateHeaders(IMessage request,
        string requestId,
        long? bucket = null,
        string? bodySha256 = null,
        bool includeBearer = true)
    {
        var resolvedBucket = bucket ?? CurrentBucket();
        var resolvedBodySha256 = bodySha256 ?? HmacRequestValidator.ComputeBodySha256(request);
        var hmac = HmacRequestValidator.ComputeHmac(Secret, MethodName, requestId, resolvedBodySha256, resolvedBucket);

        var headers = new Metadata
        {
            {
                HmacRequestValidator.RequestIdHeader, requestId
            },
            {
                HmacRequestValidator.BucketHeader, resolvedBucket.ToString(CultureInfo.InvariantCulture)
            },
            {
                HmacRequestValidator.BodySha256Header, resolvedBodySha256
            }
        };

        if (includeBearer)
        {
            headers.Add(HmacRequestValidator.AuthorizationHeader, $"Bearer {hmac}");
        }

        return headers;
    }

    private static long CurrentBucket()
    {
        return FrozenNow.ToUnixTimeSeconds() / HostAgentHmacOptions.DefaultBucketSeconds;
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

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public MutableTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return _utcNow;
        }

        public void Advance(TimeSpan delta)
        {
            _utcNow = _utcNow.Add(delta);
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
