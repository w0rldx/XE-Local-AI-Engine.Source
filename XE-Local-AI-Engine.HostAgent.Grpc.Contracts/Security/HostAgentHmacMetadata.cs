namespace XE_Local_AI_Engine.HostAgent.Grpc.Contracts.Security;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using global::Grpc.Core;
using Google.Protobuf;

/// <summary>
///     Shared contract for host agent hmac metadata.
/// </summary>
public static class HostAgentHmacMetadata
{
    public const string RequestIdHeader = "x-request-id";
    public const string BucketHeader = "x-bucket";
    public const string BodySha256Header = "x-body-sha256";
    public const string AuthorizationHeader = "authorization";

    public static Metadata Create(IMessage request,
        string methodName,
        string secret,
        TimeProvider timeProvider,
        int bucketSeconds)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(methodName);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        ArgumentNullException.ThrowIfNull(timeProvider);

        if (bucketSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bucketSeconds), bucketSeconds, "Bucket seconds must be positive.");
        }

        var requestId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var bucket = timeProvider.GetUtcNow().ToUnixTimeSeconds() / bucketSeconds;
        var bodyHash = ComputeBodySha256(request);
        var signature = ComputeHmac(secret, methodName, requestId, bodyHash, bucket);

        return
        [
            new Metadata.Entry(RequestIdHeader, requestId),
            new Metadata.Entry(BucketHeader, bucket.ToString(CultureInfo.InvariantCulture)),
            new Metadata.Entry(BodySha256Header, bodyHash),
            new Metadata.Entry(AuthorizationHeader, $"Bearer {signature}")
        ];
    }

    public static string ComputeBodySha256(IMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Convert.ToHexStringLower(SHA256.HashData(request.ToByteArray()));
    }

    public static string ComputeHmac(string secret, string methodName, string requestId, string bodySha256, long bucket)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        ArgumentException.ThrowIfNullOrWhiteSpace(methodName);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(bodySha256);

        var payload = $"{methodName}|{requestId}|{bodySha256}|{bucket}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));
    }
}
