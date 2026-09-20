namespace XE_Local_AI_Engine.Client.Services.ExternalApps;

/// <summary>
///     The placeholder a <c>secret</c> variable's VALUE is replaced with on the way out of the node, and the sentinel
///     a configure or update sends back to mean "keep what is stored".
/// </summary>
/// <remarks>
///     Stored variables hold an application's admin password and API keys; they are AEAD-encrypted at rest
///     (<c>ExternalAppInstance.VariablesJson</c>) and there is no editing reason to read one back, since the settings
///     form needs the variable DEFINITIONS, never the values. Masking is what makes the encryption meaningful against
///     anything holding a session. One symbol rather than a literal on each side: a mask the write side does not
///     recognise silently stores the placeholder as the password. The SPA states the same literal once.
/// </remarks>
public static class ExternalAppVariableMask
{
    /// <summary>
    ///     Deliberately an explicit sentinel rather than a row of bullets: a value an operator could plausibly type by
    ///     accident would silently mean "unchanged".
    /// </summary>
    /// <remarks>
    ///     An application whose variable genuinely holds this exact string keeps working — the value is preserved on
    ///     write instead of rewritten to itself.
    /// </remarks>
    public const string Value = "__XE_EXTERNAL_APP_SECRET_UNCHANGED__";
}
