namespace XE_Local_AI_Engine.Tests.Hosting;

using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Cross-language drift guard between the hubs the host maps and the hub paths vite proxies in dev.
///     <para>
///         <c>vite.config.ts</c> reads <c>config/signalr-proxy-paths.json</c> to decide which paths get the
///         WebSocket-capable proxy. A hub missing from that file has no dev transport, and because the React client
///         shares one connection manager, the resulting failure is not confined to the missing hub — it wedges the
///         WebSocket proxy and every hub in the app stops delivering. That has already shipped once: the llama.cpp
///         acquisition hub was mapped without a proxy entry and SignalR was dead across the whole dev app until it was
///         found. The file's own "keep this in sync" comment was the only thing guarding it, and a comment guards
///         nothing.
///     </para>
///     <para>
///         Both sides are read from their real sources. <c>Program.cs</c> is text-walked for its <c>MapHub</c> calls and
///         each named constant is then resolved by REFLECTION off <see cref="LocalApiRoutes" />, so a renamed or
///         retargeted constant is followed rather than silently ignored. Restating the paths as literals here would
///         compare the JSON against a third copy and pass while dev was broken.
///     </para>
/// </summary>
public sealed class SignalRProxyPathDriftTests
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(5);

    [Test]
    public async Task EveryMappedHubRoute_IsProxiedByVite()
    {
        var mapped = await ReadMappedHubRoutesAsync();
        var proxied = await ReadProxiedPathsAsync();

        AssertEx.NotEmpty(mapped, "No MapHub<...>(LocalApiRoutes....) call was found in Program.cs — re-point this guard rather than deleting it.");

        var missing = mapped.Except(proxied, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        AssertEx.Empty(missing,
            $"These hub routes are mapped by Program.cs but absent from config/signalr-proxy-paths.json: {string.Join(", ", missing)}. "
            + "vite will not proxy their WebSocket upgrade, which stalls EVERY hub in dev, not just these.");

        var stale = proxied.Except(mapped, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        AssertEx.Empty(stale,
            $"These paths are proxied but no longer mapped by Program.cs: {string.Join(", ", stale)}. "
            + "Remove them so the file keeps describing the real hub surface.");
    }

    /// <summary>Extracts every <c>MapHub</c> route constant from Program.cs and resolves it to its value.</summary>
    private static async Task<HashSet<string>> ReadMappedHubRoutesAsync()
    {
        var source = await File.ReadAllTextAsync(ResolveRepositoryFile("XE-Local-AI-Engine.Client", "Program.cs"));
        var matches = Regex.Matches(source,
            @"MapHub<\w+>\(\s*LocalApiRoutes\.(?<path>[A-Za-z0-9_.]+)\s*\)",
            RegexOptions.ExplicitCapture,
            RegexTimeout);

        var routes = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in matches)
        {
            routes.Add(ResolveRouteConstant(match.Groups["path"].Value));
        }

        return routes;
    }

    /// <summary>
    ///     Walks a dotted constant reference such as <c>ModelFit.DownloadHub</c> down from
    ///     <see cref="LocalApiRoutes" />, failing loudly if a segment no longer exists.
    /// </summary>
    private static string ResolveRouteConstant(string dottedPath)
    {
        var segments = dottedPath.Split('.');
        var current = typeof(LocalApiRoutes);

        for (var index = 0; index < segments.Length - 1; index++)
        {
            var nested = current.GetNestedType(segments[index], BindingFlags.Public | BindingFlags.NonPublic);
            current = AssertEx.NotNull(nested, $"LocalApiRoutes has no nested type '{segments[index]}' (from '{dottedPath}').");
        }

        var field = current.GetField(segments[^1], BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        var resolved = AssertEx.NotNull(field, $"LocalApiRoutes.{dottedPath} does not exist.").GetRawConstantValue() as string;

        return AssertEx.NotNull(resolved, $"LocalApiRoutes.{dottedPath} is not a string constant.");
    }

    private static async Task<HashSet<string>> ReadProxiedPathsAsync()
    {
        var json = await File.ReadAllTextAsync(ResolveRepositoryFile("XE-Local-AI-Engine.Client.React", "config", "signalr-proxy-paths.json"));
        var paths = JsonSerializer.Deserialize<string[]>(json) ?? [];
        return new HashSet<string>(paths, StringComparer.Ordinal);
    }

    // Walks up from the test binary to the repo root, matching OnboardingTourKeyDriftTests / AppUpdateContractTests.
    private static string ResolveRepositoryFile(params string[] relativeSegments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. relativeSegments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate {Path.Combine(relativeSegments)} by walking up from {AppContext.BaseDirectory}.");
    }
}
