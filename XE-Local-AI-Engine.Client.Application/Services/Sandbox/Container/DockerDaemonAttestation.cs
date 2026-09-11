namespace XE_Local_AI_Engine.Client.Services.Sandbox.Container;

using System.Text.Json.Serialization;

/// <summary>
///     The pinned daemon this node has approved. Written once on first use and thereafter only by an
///     explicit operator confirmation.
///     <para>
///         The property this record exists to defend: <c>DOCKER_HOST</c> is an ordinary environment variable, so the
///         daemon a later run reaches is not necessarily the daemon an earlier run was approved against. Since a
///         Development Mode container gets the repository bind-mounted into it and runs the repository's build and
///         test commands, a silently substituted daemon is a silently substituted execution host. Recording the
///         endpoint alone would not catch it — the same URI can front a different daemon, and the same daemon can move
///         — which is why <see cref="DaemonId" /> is the pinned value and the endpoint is context.
///     </para>
/// </summary>
public sealed record DockerDaemonAttestation
{
    /// <summary>The approved daemon's installation id.</summary>
    [JsonPropertyName("daemonId")]
    public required string DaemonId { get; init; }

    /// <summary>
    ///     The endpoint URI the daemon was approved at, as a string so the record survives a URI-shape change. Always
    ///     redacted: any user information is stripped on the way in.
    ///     <para>
    ///         The redaction is here rather than at any one reader because every reader holds one of these records and
    ///         none of them shares a formatting helper: the operator's identity-change message, the Development Mode
    ///         API mapper and the application-container resolver each interpolate this string directly. A pin written
    ///         before the redaction existed carries whatever <c>DOCKER_HOST</c> carried — <c>tcp://user:secret@host</c>
    ///         is a value an operator can set — so the file on disk is exactly the case a redaction applied only when
    ///         writing would miss.
    ///     </para>
    /// </summary>
    [JsonPropertyName("endpoint")]
    public required string Endpoint
    {
        get;
        init => field = DockerDaemonEndpoint.Redact(value);
    } = string.Empty;

    /// <summary>How that endpoint had been arrived at when it was approved.</summary>
    [JsonPropertyName("endpointSource")]
    public required DockerDaemonEndpointSource EndpointSource { get; init; }

    /// <summary>The engine version observed at approval. Context for the operator; not part of the match.</summary>
    [JsonPropertyName("serverVersion")]
    public required string ServerVersion { get; init; }

    /// <summary>When the approval happened.</summary>
    [JsonPropertyName("confirmedAtUtc")]
    public required DateTimeOffset ConfirmedAtUtc { get; init; }

    /// <summary>
    ///     Whether the approval was the implicit first-use pin or an explicit operator confirmation. Surfaced so an
    ///     operator can tell "this node has never been asked" from "this node was asked and answered".
    /// </summary>
    [JsonPropertyName("confirmedByOperator")]
    public required bool ConfirmedByOperator { get; init; }

    /// <summary>Whether <paramref name="identity" /> is the daemon this attestation approved.</summary>
    public bool Matches(DockerDaemonIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        // Identity only. A daemon that moved from one socket to another is the same daemon and must not nag; a
        // different daemon at the same socket is a substitution and must. Comparing the endpoint too would invert
        // both of those.
        return string.Equals(DaemonId, identity.DaemonId, StringComparison.Ordinal);
    }
}
