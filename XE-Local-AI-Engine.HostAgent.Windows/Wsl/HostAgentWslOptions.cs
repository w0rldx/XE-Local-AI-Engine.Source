namespace XE_Local_AI_Engine.HostAgent.Windows.Wsl;

/// <summary>
///     Configuration options for host agent wsl behavior.
/// </summary>
public sealed class HostAgentWslOptions
{
    public const string SectionName = "HostAgent:Wsl";

    public string WslExePath { get; set; } = "wsl.exe";

    public string DistroName { get; set; } = "xe-engine-runtime";

    public string InstallPath { get; set; } = string.Empty;

    public string RootfsTarballPath { get; set; } = string.Empty;

    public TimeSpan DefaultCommandTimeout { get; set; } = TimeSpan.FromSeconds(60);

    public TimeSpan ScriptCommandTimeout { get; set; } = TimeSpan.FromMinutes(10);

    public TimeSpan SupervisorInterval { get; set; } = TimeSpan.FromSeconds(15);

    public static void Bind(HostAgentWslOptions options, IConfiguration configuration, HostAgentWindowsPaths paths)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(paths);

        configuration.GetSection(SectionName).Bind(options);

        if (string.IsNullOrWhiteSpace(options.InstallPath))
        {
            options.InstallPath = Path.Combine(paths.RootDirectory, "wsl", options.DistroName);
        }

        if (string.IsNullOrWhiteSpace(options.RootfsTarballPath))
        {
            options.RootfsTarballPath = Path.Combine(paths.RootDirectory, "rootfs", "ubuntu.tar.gz");
        }
    }
}
