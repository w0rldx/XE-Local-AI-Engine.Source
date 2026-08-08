namespace XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Node-scoped persistence for the user-defined custom tool library. <c>Description</c> and the kind-specific
///     <c>ConfigJson</c> (which carries the secret header/env values) are encrypted at rest by the node encryption
///     interceptors; reads return them decrypted on the record types below. This store performs no content validation —
///     that is the application-layer service's responsibility (MAF-safe name, executable denylist, SSRF guard, danger
///     acknowledgement); it owns only id/version/timestamp stamping and the content-affecting version-bump rule.
/// </summary>
public interface ICustomToolStore
{
    /// <summary>
    ///     Persists a new custom tool (assigning <c>Id</c>, <c>CreatedAtUtc</c>, <c>UpdatedAtUtc</c> and
    ///     <c>Version = 1</c>) and returns the stored record with the encrypted columns decrypted.
    /// </summary>
    Task<CustomToolRecord> CreateAsync(CustomToolInput input, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Applies <paramref name="input" /> to the tool identified by <paramref name="id" />, stamping
    ///     <c>UpdatedAtUtc</c> and incrementing <c>Version</c> only when a content-affecting field changed (Name,
    ///     Description, Kind, Mode, parameters or config — never the <c>Enabled</c> or <c>Acknowledged</c> toggle).
    ///     Returns the updated record, or <c>null</c> when no tool has that id.
    /// </summary>
    Task<CustomToolRecord?> UpdateAsync(Guid id, CustomToolInput input, CancellationToken cancellationToken = default);

    /// <summary>Removes the tool with <paramref name="id" />. Returns <c>true</c> when a row was deleted.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Returns the record for <paramref name="id" /> with its encrypted columns decrypted, or <c>null</c> when no tool has that id.</summary>
    Task<CustomToolRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Returns every custom tool in the library, ordered by Name (Ordinal) for a stable list.</summary>
    Task<IReadOnlyList<CustomToolRecord>> ListAsync(CancellationToken cancellationToken = default);
}

/// <summary>
///     Decrypted, typed projection of a persisted custom tool. <see cref="Description" /> and <see cref="ConfigJson" />
///     are returned in plaintext (decrypted on materialization); the store converts to and from this shape at the
///     boundary so callers never touch the encrypted byte columns. <see cref="ConfigJson" /> still carries any secret
///     header/env values in the clear on the read side — the CRUD read path is responsible for masking them before they
///     reach an operator or the model.
/// </summary>
public sealed record CustomToolRecord(
    Guid Id,
    string Name,
    string Description,
    CustomToolKind Kind,
    CustomToolMode Mode,
    string ParametersJson,
    string ConfigJson,
    bool Enabled,
    bool Acknowledged,
    int Version,
    long CreatedAtUtc,
    long UpdatedAtUtc);

/// <summary>
///     Mutable fields of a custom tool supplied on create/update. Free text is passed as plaintext strings; the store
///     encodes <see cref="Description" /> and <see cref="ConfigJson" /> to UTF-8 bytes before the interceptors encrypt
///     them. <see cref="ParametersJson" /> and <see cref="ConfigJson" /> are opaque JSON to the store — the application
///     service owns their shape and validation.
/// </summary>
public sealed record CustomToolInput(
    string Name,
    string Description,
    CustomToolKind Kind,
    CustomToolMode Mode,
    string ConfigJson,
    string ParametersJson = "[]",
    bool Enabled = true,
    bool Acknowledged = false);
