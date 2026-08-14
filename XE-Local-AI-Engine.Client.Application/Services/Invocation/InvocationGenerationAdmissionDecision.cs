namespace XE_Local_AI_Engine.Client.Services.Invocation;

/// <summary>Allows generation or rejects it with a stable reason code mapped to runner-authored user-facing text.</summary>
public sealed record InvocationGenerationAdmissionDecision
{
    private const int MaxReasonCodeLength = 64;

    private InvocationGenerationAdmissionDecision(bool isAllowed, string? rejectionReasonCode)
    {
        IsAllowed = isAllowed;
        RejectionReasonCode = rejectionReasonCode;
    }

    public bool IsAllowed { get; }

    public string? RejectionReasonCode { get; }

    public static InvocationGenerationAdmissionDecision Allow { get; } = new(isAllowed: true, rejectionReasonCode: null);

    public static InvocationGenerationAdmissionDecision Reject(string rejectionReasonCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rejectionReasonCode);

        var normalizedCode = rejectionReasonCode.Trim();
        if (normalizedCode.Length > MaxReasonCodeLength
            || normalizedCode.Any(static value => value is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '_'))
        {
            normalizedCode = InvocationGenerationAdmissionReasonCodes.Unknown;
        }

        return new InvocationGenerationAdmissionDecision(isAllowed: false, normalizedCode);
    }
}

/// <summary>Stable pre-generation rejection reasons recognized by the invocation runner.</summary>
public static class InvocationGenerationAdmissionReasonCodes
{
    public const string EffectiveContextUnavailable = "effective_context_unavailable";

    public const string EffectiveContextInsufficient = "effective_context_insufficient";

    public const string Unknown = "unknown";
}
