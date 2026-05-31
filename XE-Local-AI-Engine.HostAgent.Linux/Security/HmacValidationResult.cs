namespace XE_Local_AI_Engine.HostAgent.Linux.Security;

using global::Grpc.Core;

/// <summary>
///     Value object carrying hmac validation result data.
/// </summary>
public sealed record HmacValidationResult
{
    private HmacValidationResult(bool succeeded, StatusCode statusCode, string detail)
    {
        Succeeded = succeeded;
        StatusCode = statusCode;
        Detail = detail;
    }

    public bool Succeeded { get; }

    public StatusCode StatusCode { get; }

    public string Detail { get; }

    public static HmacValidationResult Success { get; } = new(true, StatusCode.OK, string.Empty);

    public static HmacValidationResult Unauthenticated(string detail)
    {
        return new HmacValidationResult(false, StatusCode.Unauthenticated, detail);
    }

    public static HmacValidationResult AlreadyExists(string detail)
    {
        return new HmacValidationResult(false, StatusCode.AlreadyExists, detail);
    }
}
