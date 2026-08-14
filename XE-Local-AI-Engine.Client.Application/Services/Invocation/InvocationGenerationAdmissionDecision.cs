namespace XE_Local_AI_Engine.Client.Services.Invocation;

/// <summary>Allows generation or rejects it with caller-supplied, sanitized user-facing text.</summary>
public sealed record InvocationGenerationAdmissionDecision
{
    private InvocationGenerationAdmissionDecision(bool isAllowed, string? sanitizedReason)
    {
        IsAllowed = isAllowed;
        SanitizedReason = sanitizedReason;
    }

    public bool IsAllowed { get; }

    public string? SanitizedReason { get; }

    public static InvocationGenerationAdmissionDecision Allow { get; } = new(isAllowed: true, sanitizedReason: null);

    public static InvocationGenerationAdmissionDecision Reject(string sanitizedReason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sanitizedReason);
        return new InvocationGenerationAdmissionDecision(isAllowed: false, sanitizedReason.Trim());
    }
}
