namespace XE_Local_AI_Engine.Testing.FakeDocker.Endpoints;

using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;

/// <summary>
///     <c>POST /images/create?fromImage=&amp;tag=</c>. Streams the scripted progress lines as newline-delimited JSON,
///     exactly as the daemon does, and makes the image present when the stream carried no error.
///     <para>
///         The client splits a digest-pinned reference at <c>@sha256:</c> and sends the halves as
///         <c>fromImage=busybox</c> and <c>tag=sha256:…</c>, so the reference is rejoined here with <c>@</c> when the
///         tag is a digest and with <c>:</c> otherwise. Rejoining it wrongly would leave the image present under a
///         name no later <c>ImageExistsAsync</c> asks for.
///     </para>
/// </summary>
internal static class ImagePullEndpoint
{
    public static async Task HandleAsync(HttpContext context, FakeDockerState state)
    {
        var fromImage = context.Request.Query["fromImage"].ToString();
        var tag = context.Request.Query["tag"].ToString();

        if (string.IsNullOrWhiteSpace(fromImage))
        {
            await FakeDockerEndpointMapper.WriteErrorAsync(context, StatusCodes.Status400BadRequest, "fromImage is required");
            return;
        }

        var reference = Rejoin(fromImage, tag);

        var lines = state.ResolvePull(reference);

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json";

        var failed = false;
        foreach (var line in lines)
        {
            failed |= line.Error is not null;

            await context.Response.WriteAsync(ToJson(line).ToJsonString(), context.RequestAborted);
            await context.Response.WriteAsync("\n", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
        }

        if (!failed)
        {
            // GetOrAdd, never an assignment: a test that seeded this image with the volumes it declares would
            // otherwise have them erased by the very pull that is supposed to make it present.
            state.Images.GetOrAdd(reference,
                key => new FakeDockerImage
                {
                    Reference = key
                });
        }
    }

    /// <summary>
    ///     The image reference the two query parameters were split from: <c>@</c> before a digest and <c>:</c>
    ///     before a tag. Rejoining it wrongly would leave the image present under a name no later
    ///     <c>ImageExistsAsync</c> asks for.
    /// </summary>
    private static string Rejoin(string fromImage, string tag)
    {
        if (string.IsNullOrEmpty(tag))
        {
            return fromImage;
        }

        var separator = tag.StartsWith("sha256:", StringComparison.Ordinal) ? "@" : ":";
        return fromImage + separator + tag;
    }

    private static JsonObject ToJson(FakeDockerPullLine line)
    {
        var message = new JsonObject();

        if (line.Id is not null)
        {
            message["id"] = line.Id;
        }

        if (line.Status is not null)
        {
            message["status"] = line.Status;
        }

        if (line.Current is not null || line.Total is not null)
        {
            var detail = new JsonObject();
            if (line.Current is { } current)
            {
                detail["current"] = current;
            }

            if (line.Total is { } total)
            {
                detail["total"] = total;
            }

            message["progressDetail"] = detail;
        }

        if (line.Error is not null)
        {
            // errorDetail, not error: JSONMessage.Error binds to errorDetail and the aggregator reads its Message.
            message["errorDetail"] = new JsonObject
            {
                ["message"] = line.Error
            };
        }

        return message;
    }
}
