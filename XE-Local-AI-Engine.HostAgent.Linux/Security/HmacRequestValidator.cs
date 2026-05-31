namespace XE_Local_AI_Engine.HostAgent.Linux.Security;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using global::Grpc.Core;
using Google.Protobuf;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.HostAgent.Grpc.Contracts.Security;

/// <summary>
///     Startup/options validator for hmac request settings.
/// </summary>
public sealed class HmacRequestValidator
{
    public const string RequestIdHeader = HostAgentHmacMetadata.RequestIdHeader;
    public const string BucketHeader = HostAgentHmacMetadata.BucketHeader;
    public const string BodySha256Header = HostAgentHmacMetadata.BodySha256Header;
    public const string AuthorizationHeader = HostAgentHmacMetadata.AuthorizationHeader;

    private readonly IOptionsMonitor<HostAgentHmacOptions> _options;
    private readonly ReplayWindowCache _replayWindowCache;
    private readonly TimeProvider _timeProvider;

    public HmacRequestValidator(IOptionsMonitor<HostAgentHmacOptions> options,
        ReplayWindowCache replayWindowCache,
        TimeProvider timeProvider)
    {
        _options = options;
        _replayWindowCache = replayWindowCache;
        _timeProvider = timeProvider;
    }

    public HmacValidationResult Validate<TRequest>(TRequest request, Metadata headers, string methodName)
        where TRequest : class
    {
        var options = _options.CurrentValue;

        if (string.IsNullOrWhiteSpace(options.Secret))
        {
            return HmacValidationResult.Unauthenticated("HostAgent HMAC secret is not configured.");
        }

        var requestId = GetHeader(headers, RequestIdHeader);
        var bucketText = GetHeader(headers, BucketHeader);
        var bodyHash = GetHeader(headers, BodySha256Header);
        var authorization = GetHeader(headers, AuthorizationHeader);

        if (string.IsNullOrWhiteSpace(requestId)
            || string.IsNullOrWhiteSpace(bucketText)
            || string.IsNullOrWhiteSpace(bodyHash)
            || string.IsNullOrWhiteSpace(authorization))
        {
            return HmacValidationResult.Unauthenticated("Missing HostAgent HMAC headers.");
        }

        if (!long.TryParse(bucketText, NumberStyles.None, CultureInfo.InvariantCulture, out var bucket))
        {
            return HmacValidationResult.Unauthenticated("Invalid HostAgent HMAC bucket.");
        }

        var currentBucket = _timeProvider.GetUtcNow().ToUnixTimeSeconds() / options.BucketSeconds;
        if (bucket != currentBucket)
        {
            return HmacValidationResult.Unauthenticated("Expired HostAgent HMAC bucket.");
        }

        var computedBodyHash = ComputeBodySha256(request);
        if (!FixedTimeEquals(bodyHash, computedBodyHash))
        {
            return HmacValidationResult.Unauthenticated("HostAgent HMAC body hash mismatch.");
        }

        var expectedToken = ComputeHmac(options.Secret, methodName, requestId, computedBodyHash, bucket);
        var providedToken = ExtractBearerToken(authorization);
        if (string.IsNullOrWhiteSpace(providedToken) || !FixedTimeEquals(providedToken, expectedToken))
        {
            return HmacValidationResult.Unauthenticated("HostAgent HMAC signature mismatch.");
        }

        return _replayWindowCache.TryRegister(bucket, currentBucket, requestId, options.MaxRequestIdsPerBucket)
            ? HmacValidationResult.Success
            : HmacValidationResult.AlreadyExists("HostAgent HMAC request id replay detected.");
    }

    public static string ComputeBodySha256<TRequest>(TRequest request) where TRequest : class
    {
        var bytes = request is IMessage message
            ? message.ToByteArray()
            : Encoding.UTF8.GetBytes(request.ToString() ?? string.Empty);

        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    public static string ComputeHmac(string secret, string methodName, string requestId, string bodySha256, long bucket)
    {
        return HostAgentHmacMetadata.ComputeHmac(secret, methodName, requestId, bodySha256, bucket);
    }

    private static string? GetHeader(Metadata headers, string key)
    {
        return headers.FirstOrDefault(entry => string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase))?.Value;
    }

    private static string? ExtractBearerToken(string authorization)
    {
        const string bearerPrefix = "Bearer ";
        return authorization.StartsWith(bearerPrefix, StringComparison.Ordinal)
            ? authorization[bearerPrefix.Length..]
            : null;
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}
