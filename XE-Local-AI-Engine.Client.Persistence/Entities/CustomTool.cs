namespace XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed record class CustomTool
{
    public Guid Id { get; set; }

    /// <summary>
    ///     MAF tool name (identifier/routing surface) in the <c>custom__{slug}</c> form. Plaintext for list/lookup;
    ///     NOCASE-unique. Not part of the encrypted surface — mirrors <see cref="AgentSkill.Name" />.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    ///     Model-facing tool description as UTF-8 bytes — the summary the model reads to decide whether to call the tool.
    ///     Plaintext while tracked in memory; encrypted at rest by <see cref="NodeEncryptionSaveChangesInterceptor" /> and
    ///     decrypted by <see cref="NodeEncryptionMaterializationInterceptor" /> using AAD column name <c>description</c>.
    ///     Required.
    /// </summary>
    public byte[] Description { get; set; } = [];

    /// <summary>Backing int for <see cref="CustomToolKind" />; HttpFetch vs Command. Plaintext (structural).</summary>
    public int Kind { get; set; }

    /// <summary>Backing int for <see cref="CustomToolMode" />; Fixed vs Parameterized. Plaintext (structural).</summary>
    public int Mode { get; set; }

    /// <summary>
    ///     Declared input parameters as a single UTF-8 JSON array — <c>[{name,type,description,required}]</c> — compiled
    ///     downstream into a GBNF-safe schema when <see cref="Mode" /> is Parameterized, and empty (<c>[]</c>) for a Fixed
    ///     tool. Plaintext (structural — a parameter declaration carries no secret): the shape the model fills in is not
    ///     sensitive, only the values substituted at run time are. Config-affecting (bumps <see cref="Version" />).
    /// </summary>
    public string ParametersJson { get; set; } = "[]";

    /// <summary>
    ///     Kind-specific configuration as a single UTF-8 JSON object. For HttpFetch:
    ///     <c>{method, urlTemplate, headers[{name,value,isSecret}], bodyTemplate, allowedHosts[]}</c>. For Command:
    ///     <c>{executable, argsTemplate[], workingDirectory, timeoutSeconds, env[{name,value,isSecret}]}</c>. This column
    ///     carries the secret header/env values, so the whole column is encrypted at rest by
    ///     <see cref="NodeEncryptionSaveChangesInterceptor" /> and decrypted by
    ///     <see cref="NodeEncryptionMaterializationInterceptor" /> using AAD column name <c>custom_tool_config_json</c> —
    ///     the same posture as the secret-bearing MCP <c>env</c>/<c>arguments</c> columns. Required; config-affecting
    ///     (bumps <see cref="Version" />).
    /// </summary>
    public byte[] ConfigJson { get; set; } = [];

    /// <summary>
    ///     Library-wide on/off switch. Plaintext (structural). A disabled tool is never offered even when still assigned
    ///     to an agent. Default <c>true</c>; toggling it does NOT bump <see cref="Version" /> — membership in the offered
    ///     set already covers it in the runtime config hash, mirroring <see cref="AgentSkill.Enabled" />.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    ///     Operator danger acknowledgement, enforced server-side on create/update. Plaintext (structural). A gate on
    ///     authoring a host-execution tool, not model-facing content, so toggling it does NOT bump <see cref="Version" />
    ///     or invalidate a resumed run. Default <c>false</c>.
    /// </summary>
    public bool Acknowledged { get; set; }

    /// <summary>
    ///     Bumped on a content-affecting edit (Name, Description, Kind, Mode, parameters or config); drives the runtime
    ///     config hash so editing a tool invalidates resume and its approval memo. The <see cref="Enabled" /> and
    ///     <see cref="Acknowledged" /> toggles do not bump it. Default <c>1</c>.
    /// </summary>
    public int Version { get; set; }

    public long CreatedAtUtc { get; set; }

    public long UpdatedAtUtc { get; set; }
}
