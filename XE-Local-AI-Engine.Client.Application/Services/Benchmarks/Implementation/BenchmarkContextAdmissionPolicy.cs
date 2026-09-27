namespace XE_Local_AI_Engine.Client.Services.Benchmarks.Implementation;

using XE_Local_AI_Engine.Client.Services.Invocation;

public sealed class BenchmarkContextAdmissionPolicy : IInvocationGenerationAdmissionPolicy
{
    private readonly int _requiredContextTokens;

    public BenchmarkContextAdmissionPolicy(int requiredContextTokens)
    {
        _requiredContextTokens = requiredContextTokens > 0
            ? requiredContextTokens
            : throw new ArgumentOutOfRangeException(nameof(requiredContextTokens));
    }

    public int? EffectiveContextTokens { get; private set; }

    public Task<InvocationGenerationAdmissionDecision> EvaluateAsync(InvocationGenerationAdmissionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        EffectiveContextTokens = context.EffectiveContextTokens;
        return Task.FromResult(context.EffectiveContextTokens switch
        {
            null => InvocationGenerationAdmissionDecision.Reject(InvocationGenerationAdmissionReasonCodes.EffectiveContextUnavailable),
            < 1 => InvocationGenerationAdmissionDecision.Reject(InvocationGenerationAdmissionReasonCodes.EffectiveContextUnavailable),
            var effective when effective < _requiredContextTokens =>
                InvocationGenerationAdmissionDecision.Reject(InvocationGenerationAdmissionReasonCodes.EffectiveContextInsufficient),
            _ => InvocationGenerationAdmissionDecision.Allow
        });
    }
}
