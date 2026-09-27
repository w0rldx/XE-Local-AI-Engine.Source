namespace XE_Local_AI_Engine.Client.Common;

/// <summary>
///     The one-over-the-limit read every replaying feed shares: a hub's subscribe snapshot and a paged event endpoint.
/// </summary>
/// <remarks>
///     The read asks for one row past <c>limit</c>, so "there is more" is OBSERVED rather than inferred from a full page,
///     which would report more for the last page whenever the log happens to be a multiple of the limit. The watermark a
///     caller resumes from stays with the caller: the feeds do not agree on it.
/// </remarks>
public static class ReplayWindow
{
    public static async Task<(IReadOnlyList<T> Items, bool Truncated)> ReadAsync<T>(int limit, Func<int, Task<IReadOnlyList<T>>> read)
    {
        ArgumentNullException.ThrowIfNull(read);

        var rows = await read(limit + 1);
        return rows.Count > limit ? ([.. rows.Take(limit)], true) : (rows, false);
    }
}
