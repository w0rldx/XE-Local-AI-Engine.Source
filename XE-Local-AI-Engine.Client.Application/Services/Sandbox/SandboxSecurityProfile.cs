namespace XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>The node-wide sandbox security profile in EFFECT: what every sandbox create site and the isolation summary read.</summary>
/// <remarks>
///     Two values, because there is one rule (<see cref="SandboxSecurityProfilePolicy" />). The stored setting has more states (undecided,
///     <c>pending</c>), and every one of them reads as <see cref="Low" />, so a node that has not chosen behaves exactly as before the
///     profile existed (ADR 0020).
/// </remarks>
public enum SandboxSecurityProfile
{
    /// <summary>Today's behaviour: a containment axis a workload prefers is requested wherever the host advertises it, and degrades where it does not.</summary>
    Low = 0,

    /// <summary>Every axis a workload's declaration prefers becomes a precondition, refused fail-closed where the host cannot serve it.</summary>
    High = 1
}
