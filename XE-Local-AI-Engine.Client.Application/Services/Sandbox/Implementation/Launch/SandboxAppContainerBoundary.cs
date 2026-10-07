namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch;

/// <summary>
///     The measured ingredients of the Windows AppContainer filesystem boundary, the mechanism behind
///     <see cref="SandboxProviderCapabilities.SupportsAppContainerBoundary" />.
/// </summary>
/// <remarks>
///     Measured whenever the host is Windows, whatever the preview setting: measurement is a read-only probe, and ELIGIBILITY is decided per
///     call from <see cref="IExecutionPreviewPolicy" />, never baked into the cached containment. Same shape idea as
///     <c>SandboxFilesystemIsolation</c>: present means the probe succeeded, absent means
///     <see cref="SandboxContainment.AppContainerBoundaryUnavailableReason" /> says why.
/// </remarks>
internal sealed record SandboxAppContainerBoundary
{
    /// <summary>The mechanism name, e.g. <c>mxc-processcontainer</c>.</summary>
    public required string Mechanism { get; init; }

    /// <summary>The isolation tier the SDK reported, e.g. <c>AppContainerDacl</c>.</summary>
    public required string Tier { get; init; }

    /// <summary>The mechanism's maturity; <see cref="SandboxMechanismMaturity.Preview" /> for MXC.</summary>
    public required SandboxMechanismMaturity Maturity { get; init; }

    /// <summary>The probe's warnings verbatim (host-preparation recommendations and the like).</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
