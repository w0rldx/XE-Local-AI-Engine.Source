namespace XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;

public sealed class ListLocalModelsResponse
{
    public required bool IsAvailable { get; init; }

    public string? SelectedModelName { get; init; }

    public string? ConfiguredDefaultModelName { get; init; }

    public string? Error { get; init; }

    public required IReadOnlyList<LocalModelResponse> Items { get; init; }
}

public sealed class GetLocalModelDetailsRequest
{
    public string? ModelName { get; init; }
}

public sealed class DeleteLocalModelRequest
{
    public string? ModelName { get; init; }
}

public sealed class SelectLocalModelRequest
{
    public string? ModelName { get; init; }
}

public sealed class PullLocalModelRequest
{
    public string? ModelName { get; init; }
}

public sealed class LocalModelResponse
{
    public required string ModelName { get; init; }

    public long? SizeBytes { get; init; }

    public long? ModifiedAtUtc { get; init; }

    public string? Family { get; init; }

    public string? ParameterSize { get; init; }

    public string? QuantizationLevel { get; init; }

    public required bool IsSelected { get; init; }

    /// <summary>Effective classification (<c>override ?? detected</c>) as a <c>ModelKind</c> string.</summary>
    public required string Kind { get; init; }

    /// <summary>Machine-detected classification as a <c>ModelKind</c> string (drives the "reset to detected" affordance).</summary>
    public required string DetectedKind { get; init; }

    /// <summary>Raw Ollama capability strings for read-only badges (e.g. <c>tools</c>, <c>vision</c>, <c>thinking</c>).</summary>
    public required IReadOnlyList<string> Capabilities { get; init; }

    /// <summary>
    ///     True when the model advertises the Ollama <c>thinking</c> capability. The composer uses this to gate the
    ///     reasoning-effort menu so a non-reasoning model is never offered (or sent) the <c>think</c> field.
    /// </summary>
    public required bool IsReasoningCapable { get; init; }

    /// <summary>
    ///     True when the model advertises the Ollama <c>tools</c> capability. The composer uses this to gate the local-tool
    ///     controls so a non-tool model is never offered tools.
    /// </summary>
    public required bool IsToolCapable { get; init; }

    /// <summary>True when an operator override is set, so the effective kind differs from the detected one.</summary>
    public required bool IsOverridden { get; init; }
}

/// <summary>
///     Request DTO for set local model kind operations. <see cref="ModelName" /> is bound from the route; <see cref="Kind" />
///     is the desired <c>ModelKind</c> value (case-insensitive).
/// </summary>
public sealed class SetModelKindRequest
{
    public string? ModelName { get; init; }

    public string? Kind { get; init; }
}

/// <summary>
///     Request DTO for reset local model kind operations (clears the operator override). <see cref="ModelName" /> is
///     bound from the route.
/// </summary>
public sealed class ResetModelKindRequest
{
    public string? ModelName { get; init; }
}

public sealed class ModelKindResponse
{
    public required string ModelName { get; init; }

    public required string Kind { get; init; }

    public required string DetectedKind { get; init; }

    public required IReadOnlyList<string> Capabilities { get; init; }

    public required bool IsOverridden { get; init; }
}

public sealed class LocalModelDetailsResponse
{
    public required string ModelName { get; init; }

    public int? MaxContextTokens { get; init; }

    public string? Template { get; init; }

    public string? System { get; init; }

    public string? License { get; init; }
}

public sealed class SelectLocalModelResponse
{
    public required string SelectedModelName { get; init; }
}

public sealed class PullLocalModelResponse
{
    public required string ModelName { get; init; }

    public required string Status { get; init; }

    public long? TotalBytes { get; init; }

    public long? CompletedBytes { get; init; }
}

public sealed class DeleteLocalModelResponse
{
    public required string ModelName { get; init; }

    public required bool Deleted { get; init; }
}

/// <summary>
///     Response for <c>GET models/running</c>: the models the runtime currently holds in memory. Mirrors the
///     availability/error shape of <see cref="ListLocalModelsResponse" /> so the page degrades gracefully when the
///     provider is unreachable (empty list, <see cref="IsAvailable" /> false).
/// </summary>
public sealed class RunningLocalModelsResponse
{
    public required bool IsAvailable { get; init; }

    public string? Error { get; init; }

    public required IReadOnlyList<RunningLocalModelResponse> Items { get; init; }
}

/// <summary>A single loaded model, with its memory footprint and eviction time when the runtime reports them.</summary>
public sealed class RunningLocalModelResponse
{
    public required string ModelName { get; init; }

    /// <summary>Total resident size in bytes (RAM + VRAM); null when the runtime did not report it.</summary>
    public long? SizeBytes { get; init; }

    /// <summary>Portion resident in GPU VRAM in bytes; null when the runtime did not report it.</summary>
    public long? SizeVramBytes { get; init; }

    /// <summary>Scheduled eviction time as Unix epoch milliseconds (UTC); null when the runtime did not report it.</summary>
    public long? ExpiresAtUtc { get; init; }
}

/// <summary>Request DTO for <c>POST models/{modelName}/unload</c>. <see cref="ModelName" /> is bound from the route.</summary>
public sealed class UnloadLocalModelRequest
{
    public string? ModelName { get; init; }
}

/// <summary>Response for a graceful in-memory unload. Idempotent: a model that was not loaded still reports success.</summary>
public sealed class UnloadLocalModelResponse
{
    public required string ModelName { get; init; }

    public required bool Unloaded { get; init; }
}

/// <summary>
///     A single sanitized progress event emitted by <c>POST models/pull/stream</c>.  Contains only the four safe
///     fields — no paths, tokens, or raw Ollama payloads are forwarded to the client.  <see cref="Error" /> is set
///     only on the terminal failure line (<c>Status == "error"</c>) and carries a short, sanitized reason.
/// </summary>
public sealed class PullStreamProgressEvent
{
    public required string Status { get; init; }

    public long? CompletedBytes { get; init; }

    public long? TotalBytes { get; init; }

    /// <summary>Short, sanitized failure reason — present only on the terminal <c>Status == "error"</c> line.</summary>
    public string? Error { get; init; }
}
