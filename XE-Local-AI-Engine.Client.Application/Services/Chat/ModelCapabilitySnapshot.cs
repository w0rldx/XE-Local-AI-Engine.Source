namespace XE_Local_AI_Engine.Client.Services.Chat;

using System.Runtime.InteropServices;

/// <summary>
///     One model's advertised <c>thinking</c>/<c>tools</c> capabilities plus its provider locality, all resolved from a
///     single provider-routing decision. <see cref="IsCloud" /> feeds ONLY the private-data gates; the capability flags
///     are independent of it, so a fail-closed locality never disturbs reasoning or tool detection.
///     <para>
///         A value type on purpose: <c>default</c> is the safe not-capable, node-local answer, so an unresolved value
///         can never be a null that faults a caller reading a flag.
///     </para>
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly record struct ModelCapabilitySnapshot(bool SupportsThinking, bool SupportsTools, bool IsCloud);
