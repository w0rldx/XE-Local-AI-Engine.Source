namespace XE_Local_AI_Engine.Client.Services.WorkSessions;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

/// <summary>
///     Derives the deterministic operation id a supervisor write stamps on its event row.
/// </summary>
/// <remarks>
///     The phase rides INSIDE the id, so the <c>(session_id, operation_id)</c> idempotency index holds one row per
///     phase and a duplicate write short-circuits instead of double-appending. The supervisor adds the ATTEMPT, the
///     session's last sequence when the step began: a step re-run under the same index records its own rows, while a
///     duplicate inside one attempt still collapses. Zero keeps the per-step id the tool handlers use.
/// </remarks>
internal static class WorkSessionOperationId
{
    public static Guid For(Guid sessionId, int step, string phase, long attempt = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);

        var material = attempt == 0
            ? string.Create(CultureInfo.InvariantCulture, $"work-session:{sessionId:N}:{step}:{phase}")
            : string.Create(CultureInfo.InvariantCulture, $"work-session:{sessionId:N}:{step}@{attempt}:{phase}");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return new Guid(hash.AsSpan(start: 0, length: 16));
    }
}
