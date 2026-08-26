namespace XE_Local_AI_Engine.Client.Services.WorkSessions;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

/// <summary>
///     Derives the deterministic operation id a supervisor write stamps on its event row. The phase rides INSIDE the
///     id, so the two-column <c>(session_id, operation_id)</c> idempotency index holds one row per phase without a
///     phase column — and a step replayed after a crash short-circuits on the store's query-first path instead of
///     double-appending.
/// </summary>
internal static class WorkSessionOperationId
{
    public static Guid For(Guid sessionId, int step, string phase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);

        var material = string.Create(CultureInfo.InvariantCulture, $"work-session:{sessionId:N}:{step}:{phase}");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return new Guid(hash.AsSpan(start: 0, length: 16));
    }
}
