namespace XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     Persistence boundary for i node settings data.
/// </summary>
public interface INodeSettingsStore
{
    Task<StoredNodeSettings> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     The STRICT load: it distinguishes "there is no value yet" from "I cannot read the value", which
    ///     <see cref="LoadAsync" /> deliberately cannot.
    /// </summary>
    /// <remarks>
    ///     A MISSING file returns the default record, as on an install that has never saved; a readable file returns
    ///     the normalized record; a file that is PRESENT but unreadable returns <see langword="null" />. The default
    ///     body delegates to <see cref="LoadAsync" />, the CORRECT answer for every in-memory implementation, since a
    ///     double holding a record is readable by definition and can never be the corrupt-file case. Only
    ///     implementations that touch a file, and the decorator in front of one, override it.
    /// </remarks>
    async Task<StoredNodeSettings?> LoadStrictAsync(CancellationToken cancellationToken = default)
    {
        return await LoadAsync(cancellationToken);
    }

    /// <summary>
    ///     Synchronous load of the stored settings, used only on the composition and startup path.
    /// </summary>
    /// <remarks>
    ///     DI factory seeds and singleton constructors read it there, because blocking on the async file read would
    ///     starve the thread pool during host startup. The settings come from a tiny local JSON file, so a synchronous
    ///     read is fast and safe; the common request-time read still uses <see cref="LoadAsync" />.
    /// </remarks>
    StoredNodeSettings Load(CancellationToken cancellationToken = default);

    Task SaveAsync(StoredNodeSettings settings, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Applies <paramref name="mutate" /> to the stored settings and persists the result, with the load and the
    ///     save held under ONE lock so no other writer can interleave between them. Returns the persisted settings.
    /// </summary>
    /// <remarks>
    ///     The settings record is whole-file: every writer serializes ALL of it, so a load-modify-save that yields the
    ///     lock in the middle silently discards every field another writer changed in that window — not a theoretical
    ///     race here, since the external-provider reconciliation pass runs on every save and every boot, concurrently
    ///     with the operator editing Node Settings. <paramref name="mutate" /> runs under the lock, so it must be pure
    ///     and fast: no I/O, no awaits, no calls back into this store, and returning the same instance still writes.
    /// </remarks>
    Task<StoredNodeSettings> UpdateAsync(Func<StoredNodeSettings, StoredNodeSettings> mutate, CancellationToken cancellationToken = default);
}
