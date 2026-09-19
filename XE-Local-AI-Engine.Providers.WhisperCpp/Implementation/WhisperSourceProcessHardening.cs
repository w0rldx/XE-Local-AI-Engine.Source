namespace XE_Local_AI_Engine.Providers.WhisperCpp.Implementation;

using System.Diagnostics;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     Applies the non-interactive, secret-free environment every source-build child process runs under.
/// </summary>
/// <remarks>
///     A source build executes third-party build scripts with the app user's privileges. The environment is therefore
///     cleared rather than filtered: only the toolchain variables below survive, so an inherited token, key or proxy
///     credential cannot reach a <c>configure</c> script. Git's prompts are switched off in every channel it has —
///     terminal, askpass, credential helper, system and global config — because a build that blocks on a hidden
///     password prompt looks exactly like a build that hung.
/// </remarks>
internal static class WhisperSourceProcessHardening
{
    private const string DefaultPath = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin";
    private static readonly string[] PreservedVariables = ["PATH", "LANG", "LC_ALL", "CUDA_HOME", "CUDA_PATH"];

    internal static void Configure(ProcessStartInfo startInfo, string isolationRoot)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentException.ThrowIfNullOrWhiteSpace(isolationRoot);

        var preserved = PreservedVariables.ToDictionary(static key => key,
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

        // The checklist answers "CUDA compiler" from a conventional install that is not on PATH (see
        // CudaToolkitLocator); pin CMake to that same nvcc so a green prerequisite can never be followed by a build
        // that looks for the toolkit somewhere else. Child environment only — the host's is never touched.
        if (CudaToolkitLocator.FindNvccOutsidePath() is { } nvcc)
        {
            startInfo.Environment["CUDACXX"] = nvcc;
        }

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

    /// <summary>
    ///     Closes the child's stdin. A build tool that decides to ask a question then reads end-of-file and fails
    ///     immediately, instead of waiting forever on a prompt nobody can answer.
    /// </summary>
    internal static void CloseStandardInput(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        process.StandardInput.Close();
    }

    private static void CopyIfPresent(ProcessStartInfo startInfo,
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
