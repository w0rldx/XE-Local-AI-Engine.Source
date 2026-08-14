namespace XE_Local_AI_Engine.Client.Services.Invocation;

/// <summary>Evaluates whether a warmed invocation may begin provider generation.</summary>
public interface IInvocationGenerationAdmissionPolicy
{
    Task<InvocationGenerationAdmissionDecision> EvaluateAsync(InvocationGenerationAdmissionContext context,
        CancellationToken cancellationToken = default);
}
