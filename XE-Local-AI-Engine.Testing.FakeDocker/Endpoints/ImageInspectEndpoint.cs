namespace XE_Local_AI_Engine.Testing.FakeDocker.Endpoints;

using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;

/// <summary>
///     <c>GET /images/{reference}/json</c>. The reference is a catch-all segment because a registry-qualified one
///     carries slashes of its own (<c>ghcr.io/owner/name@sha256:…</c>), which a single-segment route would split.
///     <para>
///         The client reads nothing out of a successful response: the whole answer is present-or-404, and the 404 is
///         what Docker.DotNet turns into the <c>DockerImageNotFoundException</c> it catches.
///     </para>
/// </summary>
internal static class ImageInspectEndpoint
{
    private const string InspectSuffix = "/json";

    public static Task HandleAsync(HttpContext context, FakeDockerState state, string reference)
    {
        if (!reference.EndsWith(InspectSuffix, StringComparison.Ordinal))
        {
            return FakeDockerEndpointMapper.WriteErrorAsync(context, StatusCodes.Status404NotFound, "page not found");
        }

        var imageReference = reference[..^InspectSuffix.Length];
        if (!state.Images.ContainsKey(imageReference))
        {
            return FakeDockerEndpointMapper.WriteErrorAsync(context,
                StatusCodes.Status404NotFound,
                $"No such image: {imageReference}");
        }

        return FakeDockerEndpointMapper.WriteJsonAsync(context,
            new JsonObject
            {
                ["Id"] = "sha256:" + FakeDockerState.NewId(),
                ["RepoDigests"] = new JsonArray(imageReference),
                ["Os"] = state.OperatingSystem,
                ["Architecture"] = "amd64"
            });
    }
}
