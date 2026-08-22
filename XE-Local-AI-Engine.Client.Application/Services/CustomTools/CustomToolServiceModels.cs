namespace XE_Local_AI_Engine.Client.Services.CustomTools;

using XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Public, wire-safe projection of a custom tool's typed configuration. These records are the CRUD boundary the
///     endpoints bind against (the internal <c>HttpFetchConfig</c>/<c>CommandConfig</c> family stays inside this
///     assembly). The service owns the mapping to and from the store's opaque <c>ConfigJson</c>/<c>ParametersJson</c>
///     columns so the endpoint layer never touches the JSON serializer or the secret-masking rule.
/// </summary>
public sealed record CustomToolParameterModel
{
    public string Name { get; init; } = string.Empty;

    /// <summary>One of <c>string</c>/<c>number</c>/<c>integer</c>/<c>boolean</c>.</summary>
    public string Type { get; init; } = "string";

    public string Description { get; init; } = string.Empty;

    public bool Required { get; init; }
}

/// <summary>
///     An HTTP header a fetch tool sends. On the request path <see cref="Value" /> is the cleartext value; on the
///     response path a value marked <see cref="IsSecret" /> is replaced by <see cref="CustomToolSecrets.Sentinel" />
///     (or empty when unset) so the CRUD read never returns the stored secret.
/// </summary>
public sealed record CustomToolHeaderModel
{
    public string Name { get; init; } = string.Empty;

    public string Value { get; init; } = string.Empty;

    public bool IsSecret { get; init; }
}

/// <summary>An extra environment variable a command tool injects. Same secret-masking rule as <see cref="CustomToolHeaderModel" />.</summary>
public sealed record CustomToolEnvironmentVariableModel
{
    public string Name { get; init; } = string.Empty;

    public string Value { get; init; } = string.Empty;

    public bool IsSecret { get; init; }
}

/// <summary>Public HttpFetch configuration carried on create/update requests and echoed (secrets masked) on responses.</summary>
public sealed record HttpFetchDefinition
{
    public string Method { get; init; } = "GET";

    public string UrlTemplate { get; init; } = string.Empty;

    public IReadOnlyList<CustomToolHeaderModel> Headers { get; init; } = [];

    public string? BodyTemplate { get; init; }

    public IReadOnlyList<string> AllowedHosts { get; init; } = [];
}

/// <summary>Public Command configuration carried on create/update requests and echoed (secrets masked) on responses.</summary>
public sealed record CommandDefinition
{
    public string Executable { get; init; } = string.Empty;

    public IReadOnlyList<string> ArgsTemplate { get; init; } = [];

    public string? WorkingDirectory { get; init; }

    public int TimeoutSeconds { get; init; }

    public IReadOnlyList<CustomToolEnvironmentVariableModel> Env { get; init; } = [];
}

/// <summary>
///     The editable custom-tool payload the create/update endpoints bind. Exactly one of <see cref="Http" /> /
///     <see cref="Command" /> is populated for the matching <see cref="Kind" />. <see cref="Acknowledged" /> is the
///     server-enforced danger acknowledgement: a create/update is rejected unless it is <see langword="true" />.
/// </summary>
public sealed record CustomToolDefinition
{
    public string Name { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public CustomToolKind Kind { get; init; }

    public CustomToolMode Mode { get; init; }

    public bool Enabled { get; init; } = true;

    public bool Acknowledged { get; init; }

    public IReadOnlyList<CustomToolParameterModel> Parameters { get; init; } = [];

    public HttpFetchDefinition? Http { get; init; }

    public CommandDefinition? Command { get; init; }
}

/// <summary>
///     Full wire projection of a stored custom tool. Secret header/env values are masked (see
///     <see cref="CustomToolSecrets.Sentinel" />) — the CRUD read path never returns cleartext secrets to an operator
///     or the model.
/// </summary>
public sealed record CustomToolView
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required string Description { get; init; }

    public required CustomToolKind Kind { get; init; }

    public required CustomToolMode Mode { get; init; }

    public required bool Enabled { get; init; }

    public required bool Acknowledged { get; init; }

    public required int Version { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }

    public required IReadOnlyList<CustomToolParameterModel> Parameters { get; init; }

    public HttpFetchDefinition? Http { get; init; }

    public CommandDefinition? Command { get; init; }
}

/// <summary>Wrapper for the list endpoint so the generated client has a stable, named list-response schema.</summary>
public sealed record ListCustomToolsResponse
{
    public required IReadOnlyList<CustomToolView> Items { get; init; }
}

/// <summary>Request for the ProgramLaunch executable-probe (authoring-time validation of a candidate absolute path).</summary>
public sealed record ProbeExecutableRequest
{
    public string? Path { get; init; }
}

/// <summary>
///     Result of validating a candidate command executable at authoring time. <see cref="Ok" /> is <see langword="true" />
///     only when the path passes the same <see cref="HostExecutableGuard" /> checks the executor runs at launch; on
///     failure <see cref="Reason" /> carries the sanitized reason (no filesystem contents).
/// </summary>
public sealed record HostExecutableProbeResult(bool Ok, string? Reason, string? Path);

/// <summary>The masking sentinel returned in place of a stored secret value on the CRUD read path.</summary>
public static class CustomToolSecrets
{
    public const string Sentinel = "__secret_set__";
}
