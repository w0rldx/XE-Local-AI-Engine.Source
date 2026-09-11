namespace XE_Local_AI_Engine.Client.Services.ExternalApps;

/// <summary>
///     The placeholder a <c>secret</c> variable's VALUE is replaced with on the way out of the node, and the sentinel
///     a configure or update sends back to mean "keep what is stored".
///     <para>
///         An application's stored variables hold its admin password and its API keys. They are AEAD-encrypted at rest
///         (<c>ExternalAppInstance.VariablesJson</c>) and there is no editing reason to read one back — the settings
///         form needs the variable DEFINITIONS to render its rows, never the secret values. Masking is what makes the
///         encryption meaningful against anything holding a session rather than only against someone holding the file.
///     </para>
///     <para>
///         One symbol rather than a literal on each side: the mask-out and the keep-on-write are two halves of one
///         round-trip, and a mask the write side does not recognise silently stores the placeholder as the password.
///         The SPA states the same literal once, for the same reason.
///     </para>
/// </summary>
public static class ExternalAppVariableMask
{
    /// <summary>
    ///     Deliberately an explicit sentinel rather than a row of bullets: a value an operator could plausibly type by
    ///     accident would silently mean "unchanged". An application whose variable genuinely holds this exact string
    ///     keeps working — the value is simply preserved on write instead of rewritten to itself.
    /// </summary>
    public const string Value = "__XE_EXTERNAL_APP_SECRET_UNCHANGED__";
}
