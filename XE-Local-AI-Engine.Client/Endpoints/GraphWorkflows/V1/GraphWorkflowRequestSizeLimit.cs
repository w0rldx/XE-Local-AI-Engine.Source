namespace XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1;

using FastEndpoints;
using Microsoft.AspNetCore.Http.Metadata;

/// <summary>
///     The request-body cap for the three routes that carry a graph — create, update and validate. Without one they
///     inherit Kestrel's 30 MB default, and a body that size is parsed, walked by the runtime's parser and hashed
///     before the node cap ever gets a chance to refuse it.
///     <para>
///         Two mechanisms, as elsewhere in this tree: the metadata is what the HOST enforces before the body is read,
///         and <see cref="RefuseIfOversized" /> is the cheap early exit the handler makes for itself. The metadata is
///         Kestrel-side, so the in-memory test host neither honours nor disproves it; the early exit is the half that
///         is provable without a real connection.
///     </para>
/// </summary>
internal sealed class GraphWorkflowRequestSizeLimit : IRequestSizeLimitMetadata
{
    /// <summary>
    ///     1 MiB. A 200-node graph carrying per-node instructions and response schemas is tens of KiB, and even the
    ///     option ceiling of 10 000 nodes fits inside this with room to spare — so a body over it is not a graph an
    ///     editor drew.
    /// </summary>
    public const long MaxBytes = 1024 * 1024;

    public long? MaxRequestBodySize => MaxBytes;

    /// <summary>
    ///     Whether the request DECLARES more body than this node accepts, recording the refusal on
    ///     <paramref name="errors" /> when it does. Content-Length is never the limit — a caller can omit or lie about
    ///     it — which is what the metadata above is for; this is the layer that answers with a message an operator can
    ///     read instead of a bare host refusal.
    ///     <para>
    ///         An ABSENT Content-Length is therefore NOT a refusal. A chunked body declares no length at all, and
    ///         reading that null as "over the cap" would answer 413 to every streamed request, about a size nobody
    ///         ever stated. The streamed case belongs to the metadata above, which counts the bytes as they arrive.
    ///     </para>
    /// </summary>
    public static bool RefuseIfOversized(HttpRequest request, IValidationErrors errors)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(errors);

        if (request.ContentLength is not > MaxBytes)
        {
            return false;
        }

        errors.AddError($"The request body is larger than the {MaxBytes / (1024 * 1024)} MB this node accepts for a graph.");
        return true;
    }
}
