namespace XE_Local_AI_Engine.HostAgent.Windows;

/// <summary>
///     Value object carrying host agent windows paths data.
/// </summary>
public sealed record HostAgentWindowsPaths(
    string RootDirectory,
    string LogDirectory,
    string RuntimeMetadataPath,
    string DesiredStatePath,
    string SecretDirectory,
    string AdminTokenPath)
{
    public static HostAgentWindowsPaths CreateDefault()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrWhiteSpace(programData))
        {
            programData = Path.GetTempPath();
        }

        var rootDirectory = Path.Combine(programData, "XE-Local-AI-Engine", "host-agent");

        return new HostAgentWindowsPaths(rootDirectory,
            Path.Combine(rootDirectory, "logs"),
            Path.Combine(rootDirectory, "runtime.json"),
            Path.Combine(rootDirectory, "desired-state.json"),
            Path.Combine(rootDirectory, "secrets"),
            Path.Combine(rootDirectory, "secrets", "admin-token.dpapi"));
    }
}
