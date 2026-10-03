namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using XE_Local_AI_Engine.Client.Services.Diagnostics;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Diagnostics;

/// <summary>Registers the node-info report and the support-bundle builder behind the Diagnostics endpoints.</summary>
internal static class AddNodeDiagnosticsExtensions
{
    // The folder name every writer hard-codes: the node log, StartupCrashLog, the desktop shell and the launcher.
    private const string LogsFolder = "logs";

    public static IHostApplicationBuilder AddNodeDiagnostics(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddSingleton<INodeInfoService, NodeInfoService>();
        builder.Services.AddSingleton(static services => new SupportBundleScrubber([Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)],
            services.GetRequiredService<INodeDataDirectory>().Root,
            OperatingSystem.IsWindows()));
        builder.Services.AddSingleton<ISupportBundleService>(static services => new SupportBundleService(services.GetRequiredService<INodeInfoService>(),
            services.GetRequiredService<IChildProcessOutputTails>(),
            services.GetRequiredService<SupportBundleScrubber>(),
            ResolveLogDirectories(services.GetRequiredService<INodeDataDirectory>().Root),
            services.GetRequiredService<TimeProvider>()));
        return builder;
    }

    // The node log and startup-crash.log live under the data root; the desktop shell and the launcher write under
    // XE_DATA_DIR or, without it, the per-user LocalApplicationData folder, which differs from the root in a headless run.
    private static string[] ResolveLogDirectories(string dataRoot)
    {
        var candidates = new[]
        {
            dataRoot,
            Environment.GetEnvironmentVariable("XE_DATA_DIR"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XE-Local-AI-Engine")
        };
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        return candidates.Where(static root => !string.IsNullOrWhiteSpace(root))
                         .Select(static root => Path.GetFullPath(Path.Combine(root!, LogsFolder)))
                         .Distinct(comparer)
                         .ToArray();
    }
}
