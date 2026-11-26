namespace XE_Local_AI_Engine.Client.Services.Training.Runs;

using XE_Local_AI_Engine.Providers.Python;

/// <summary>
///     Where the shipped Python scripts live. Duplicates <c>TrainingRuntimeLayout</c>'s resolution because that type is
///     internal to the provider assembly and this is the only fact the application layer needs from it.
/// </summary>
public static class TrainingScripts
{
    public const string TrainScriptName = "train.py";

    /// <summary>The adapter merge step. The GGUF conversion beside it runs llama.cpp's own scripts, not this one.</summary>
    public const string ExportScriptName = "export.py";

    /// <summary>Model Lab's tracer. It runs on the training runtime and needs nothing beyond it.</summary>
    public const string ModelLabScriptName = "modellab.py";

    /// <summary>The training stepper's tiny CPU network. Checked by name, since <see cref="ResolveDirectory" /> keys on <see cref="TrainScriptName" />.</summary>
    public const string PlaygroundScriptName = "nnplayground.py";

    private const string PublishedScriptsDirectoryName = "training-scripts";
    private const string RepositoryScriptsRelativePath = "tools/training";

    /// <summary>Whether this process can run the scripts: the training environment resolves for Linux x64 only (<c>tool.uv.environments</c>).</summary>
    public static bool IsPlatformSupported => OperatingSystem.IsLinux() && ManagedPythonPins.IsCurrentPlatformSupported;

    public static string ResolveDirectory()
    {
        var published = Path.Combine(AppContext.BaseDirectory, PublishedScriptsDirectoryName);
        if (File.Exists(Path.Combine(published, TrainScriptName)))
        {
            return published;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, RepositoryScriptsRelativePath);
            if (File.Exists(Path.Combine(candidate, TrainScriptName)))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return published;
    }
}
