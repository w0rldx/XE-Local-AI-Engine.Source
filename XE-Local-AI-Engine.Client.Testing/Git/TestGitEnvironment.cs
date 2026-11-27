namespace XE_Local_AI_Engine.Client.Testing.Git;

using System.Diagnostics;
using System.Globalization;

/// <summary>The inherited git environment a test's child process drops, so fixture git only touches the fixture.</summary>
public static class TestGitEnvironment
{
    /// <summary>
    ///     The variables <c>scripts/lib/git-env.sh</c> lists: inherited, any one of them points the fixture's git at
    ///     another repository, work tree, index or object store than its working directory's.
    /// </summary>
    public static IReadOnlyList<string> RepositorySelectors { get; } =
    [
        "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_COMMON_DIR", "GIT_OBJECT_DIRECTORY",
        "GIT_ALTERNATE_OBJECT_DIRECTORIES", "GIT_NAMESPACE"
    ];

    /// <summary>
    ///     Inherited, <c>GIT_CONFIG</c> is the file a fixture's <c>git config</c> writes, and the others inject settings
    ///     (<c>GIT_CONFIG_COUNT</c> gates its key/value pairs; <c>GIT_TEMPLATE_DIR</c> outranks <c>init.templateDir</c>).
    /// </summary>
    public static IReadOnlyList<string> ConfigOverrides { get; } =
        ["GIT_CONFIG", "GIT_CONFIG_PARAMETERS", "GIT_CONFIG_COUNT", "GIT_TEMPLATE_DIR"];

    /// <summary>
    ///     Removes <see cref="RepositorySelectors" /> and <see cref="ConfigOverrides" /> from the child's environment,
    ///     hides the global config file and neutralises hooks, signing and the operator's ignore and attribute files.
    /// </summary>
    /// <remarks>
    ///     The system file stays: Git for Windows keeps its platform defaults (<c>core.autocrlf</c>) there. Command-level
    ///     settings outrank every file, so no hook, signing agent, file monitor, ignore or attributes file (the XDG
    ///     defaults too) or init template a config names reaches a fixture; a child shell's git inherits them.
    /// </remarks>
    public static void Isolate(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        foreach (var variable in RepositorySelectors.Concat(ConfigOverrides))
        {
            startInfo.Environment.Remove(variable);
        }

        var nullDevice = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        startInfo.Environment["GIT_CONFIG_GLOBAL"] = nullDevice;
        string[][] settings =
        [
            ["core.hooksPath", nullDevice], ["commit.gpgSign", "false"], ["tag.gpgSign", "false"],
            ["core.excludesFile", nullDevice], ["core.attributesFile", nullDevice], ["core.fsmonitor", "false"],
            ["init.templateDir", ""]
        ];
        startInfo.Environment["GIT_CONFIG_COUNT"] = settings.Length.ToString(CultureInfo.InvariantCulture);
        for (var index = 0; index < settings.Length; index++)
        {
            startInfo.Environment[$"GIT_CONFIG_KEY_{index}"] = settings[index][0];
            startInfo.Environment[$"GIT_CONFIG_VALUE_{index}"] = settings[index][1];
        }
    }
}
