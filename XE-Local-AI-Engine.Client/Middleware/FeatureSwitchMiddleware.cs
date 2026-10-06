namespace XE_Local_AI_Engine.Client.Middleware;

using Microsoft.AspNetCore.Routing.Template;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     Answers 404 for a feature's whole route family, hub included, while its node-settings switch is off, read per request so a
///     saved change applies without a restart.
/// </summary>
/// <remarks>
///     Installed ahead of local API security and authentication, so a switched-off feature cannot be probed through 401 or 403. The
///     endpoints stay discovered either way, so the OpenAPI document is the same on every node. A capability GET stays reachable so
///     the SPA can say the feature is off instead of reading a bodyless 404 as a load failure; transcription and external apps have
///     none, but transcription keeps its stop routes open so work started before the switch went off can end. Development's
///     endpoints are also filtered at discovery from the startup value.
/// </remarks>
public sealed class FeatureSwitchMiddleware
{
    private static readonly FeatureGate[] Gates =
    [
        new(Route(LocalApiRoutes.Development.Root), Route(LocalApiRoutes.Development.Capability),
            static (settings, ct) => settings.GetDevelopmentEnabledAsync(ct)),
        new(Route(LocalApiRoutes.WorkSessions.Root), Route(LocalApiRoutes.WorkSessions.Capability),
            static (settings, ct) => settings.GetWorkSessionsEnabledAsync(ct)),
        new(Route(LocalApiRoutes.DevelopmentWorkflows.Root), Route(LocalApiRoutes.DevelopmentWorkflows.Capability),
            static (settings, ct) => settings.GetDevWorkflowsEnabledAsync(ct)),
        new(Route(LocalApiRoutes.GraphWorkflows.Root), Route(LocalApiRoutes.GraphWorkflows.Capability),
            static (settings, ct) => settings.GetGraphWorkflowsEnabledAsync(ct)),
        new(Route(LocalApiRoutes.Transcription.Root), PathString.Empty,
            static (settings, ct) => settings.GetTranscriptionEnabledAsync(ct)),
        new(Route(LocalApiRoutes.ExternalApps.Root), PathString.Empty,
            static (settings, ct) => settings.GetExternalAppsEnabledAsync(ct))
    ];

    /// <summary>The (method, route) pairs that only end transcription work, so they stay reachable while the switch is off.</summary>
    private static readonly StopRoute[] TranscriptionStopRoutes =
    [
        new(HttpMethods.Delete, Template(LocalApiRoutes.Transcription.SessionProcessCapture)),
        new(HttpMethods.Post, Template(LocalApiRoutes.Transcription.SessionCancel)),
        new(HttpMethods.Post, Template(LocalApiRoutes.Transcription.ModelDownloadCancel)),
        new(HttpMethods.Post, Template(LocalApiRoutes.Transcription.RuntimeSourceBuildCancel)),
        new(HttpMethods.Post, Template(LocalApiRoutes.Transcription.RuntimeEject))
    ];

    private readonly RequestDelegate _next;

    public FeatureSwitchMiddleware(RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(next);
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, INodeRuntimeSettings runtimeSettings)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(runtimeSettings);

        var path = context.Request.Path;
        foreach (var (root, capability, isEnabled) in Gates)
        {
            if (!path.StartsWithSegments(root, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if ((!capability.HasValue || !path.Equals(capability, StringComparison.OrdinalIgnoreCase))
                && !IsTranscriptionStop(context.Request)
                && !await isEnabled(runtimeSettings, context.RequestAborted))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            break;
        }

        await _next(context);
    }

    private static bool IsTranscriptionStop(HttpRequest request) =>
        Array.Exists(TranscriptionStopRoutes, stop =>
            HttpMethods.Equals(request.Method, stop.Method) && stop.Matcher.TryMatch(request.Path, new RouteValueDictionary()));

    private static PathString Route(string relative) =>
        new($"/{LocalApiRoutes.Prefix}/{relative}");

    private static TemplateMatcher Template(string relative) =>
        new(TemplateParser.Parse($"{LocalApiRoutes.Prefix}/{relative}"), new RouteValueDictionary());

    /// <summary>One feature's route family, its always-reachable capability GET, and the switch that gates it.</summary>
    private readonly record struct FeatureGate(PathString Root, PathString Capability, Func<INodeRuntimeSettings, CancellationToken, Task<bool>> IsEnabled);

    /// <summary>A method and route template that stays reachable while its feature is off.</summary>
    private readonly record struct StopRoute(string Method, TemplateMatcher Matcher);
}
