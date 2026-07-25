namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;

using System.Diagnostics;

/// <summary>Applies the non-interactive, secret-free environment shared by source-build child processes.</summary>
internal static class StableDiffusionSourceProcessHardening
{
    private const string DefaultPath = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin";
    private static readonly string[] PreservedVariables = ["PATH", "LANG", "LC_ALL", "CUDA_HOME", "CUDA_PATH"];

    internal static void Configure(ProcessStartInfo startInfo, string isolationRoot)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentException.ThrowIfNullOrWhiteSpace(isolationRoot);

        var preserved = PreservedVariables.ToDictionary(
            static key => key,
            key => startInfo.Environment.TryGetValue(key, out var value) ? value : null,
            StringComparer.Ordinal);
        var home = Path.Combine(isolationRoot, ".process-home");
        var temp = Path.Combine(isolationRoot, ".process-tmp");
        CreateOwnerOnlyDirectory(home);
        CreateOwnerOnlyDirectory(temp);
        startInfo.Environment.Clear();
        startInfo.Environment["PATH"] = string.IsNullOrWhiteSpace(preserved["PATH"]) ? DefaultPath : preserved["PATH"];
        startInfo.Environment["LANG"] = string.IsNullOrWhiteSpace(preserved["LANG"]) ? "C" : preserved["LANG"];
        startInfo.Environment["LC_ALL"] = string.IsNullOrWhiteSpace(preserved["LC_ALL"]) ? "C" : preserved["LC_ALL"];
        CopyIfPresent(startInfo, preserved, "CUDA_HOME");
        CopyIfPresent(startInfo, preserved, "CUDA_PATH");
        startInfo.Environment["HOME"] = home;
        startInfo.Environment["TMPDIR"] = temp;
        startInfo.Environment["TERM"] = "dumb";
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GCM_INTERACTIVE"] = "Never";
        startInfo.Environment["GIT_ASKPASS"] = "/bin/false";
        startInfo.Environment["SSH_ASKPASS"] = "/bin/false";
        startInfo.Environment["SSH_ASKPASS_REQUIRE"] = "never";
        startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        startInfo.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        startInfo.Environment["GIT_CONFIG_SYSTEM"] = "/dev/null";
        startInfo.RedirectStandardInput = true;
    }

    internal static void CloseStandardInput(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        process.StandardInput.Close();
    }

    private static void CopyIfPresent(
        ProcessStartInfo startInfo,
        IReadOnlyDictionary<string, string?> preserved,
        string key)
    {
        if (!string.IsNullOrWhiteSpace(preserved[key]))
        {
            startInfo.Environment[key] = preserved[key];
        }
    }

    private static void CreateOwnerOnlyDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
