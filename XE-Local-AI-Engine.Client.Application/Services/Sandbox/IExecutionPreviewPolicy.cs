namespace XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>Whether sandbox launch mechanisms of <c>Preview</c> maturity may serve a role right now.</summary>
/// <remarks>
///     Read PER CALL (the provider's <c>Capabilities</c> getter and every sandbox create), never cached in the host containment probe: the
///     operator's switch is a stored node setting that can change under a running node, and turning it off must take effect for the next
///     sandbox without a restart. Measurement and eligibility are separate on purpose (ADR 0019).
/// </remarks>
public interface IExecutionPreviewPolicy
{
    /// <summary><see langword="true" /> while the operator has enabled execution previews.</summary>
    bool PreviewMechanismsEnabled { get; }
}
