namespace XE_Local_AI_Engine.Tests.Providers.WhisperCpp;

using System.ComponentModel;
using System.Diagnostics;
using XE_Local_AI_Engine.Providers.WhisperCpp.Options;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;
// System.ComponentModel declares its own CategoryAttribute, and a file-scoped using beats the global one.
using CategoryAttribute = TUnit.Core.CategoryAttribute;

/// <summary>
///     Proves on real bytes, at a new location, the property adoption depends on: a built <c>whisper-server</c> whose
///     tree has been moved still resolves its libraries relatively.
/// </summary>
/// <remarks>
///     Opt-in, and deliberately so. The unit suite must never compile a native runtime, so this reads a binary the
///     operator already built and points <c>XE_WHISPERCPP_SERVER_PATH</c> at. Without that binary it skips visibly
///     rather than passing on nothing.
///
///     The service-level gate in <see cref="WhisperCppSourceBuildServiceTests" /> checks what <c>readelf</c> reported;
///     this checks the bytes after the move, which is the thing that actually breaks when the rpath is wrong.
/// </remarks>
[RunOn(OS.Linux)]
[Category(TestCategories.ExternalInfra)]
public sealed class WhisperManagedRuntimeRelocationTests
{
    [Test]
    public async Task AdoptedServer_AfterTheTreeIsMoved_StillResolvesItsRunpathRelatively()
    {
        var serverPath = Environment.GetEnvironmentVariable(WhisperServerRuntimeOverrideOptions.ServerPathEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(serverPath) || !File.Exists(serverPath) || !await ReadelfIsAvailableAsync())
        {
            Skip.Test($"Set {WhisperServerRuntimeOverrideOptions.ServerPathEnvironmentVariable} to a locally built "
                      + "whisper-server, with readelf on PATH, to prove RUNPATH relocation.");
            return;
        }

        // Copy rather than move: the operator's build is not this test's to relocate, and a copy at a fresh path
        // stands in for exactly what adoption does to the staging tree.
        using var moved = new TempDirectory("xe-whisper-relocation");
        var sourceDirectory = Path.GetDirectoryName(Path.GetFullPath(serverPath))!;
        var movedServer = Path.Combine(moved.Path, Path.GetFileName(serverPath));
        File.Copy(serverPath, movedServer);
        foreach (var library in Directory.EnumerateFiles(sourceDirectory, "lib*.so*"))
        {
            File.Copy(library, Path.Combine(moved.Path, Path.GetFileName(library)));
        }

        var output = await ReadDynamicSectionAsync(movedServer);
        var entries = ParseRunpathEntries(output);

        AssertEx.NotEmpty(entries,
            "The moved binary reports no RUNPATH or RPATH at all, so it can only find its libraries in the directory "
            + "it was built in — the directory adoption deletes.");
        foreach (var entry in entries)
        {
            AssertEx.True(entry.StartsWith("$ORIGIN", StringComparison.Ordinal),
                $"Every runpath element must be $ORIGIN-relative to survive the move; found '{entry}'.");
        }
    }

    private static IReadOnlyList<string> ParseRunpathEntries(string readelfOutput)
    {
        var entries = new List<string>();
        foreach (var line in readelfOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.Contains("(RUNPATH)", StringComparison.Ordinal) && !line.Contains("(RPATH)", StringComparison.Ordinal))
            {
                continue;
            }

            var open = line.IndexOf('[', StringComparison.Ordinal);
            var close = line.LastIndexOf(']');
            if (open < 0 || close <= open)
            {
                continue;
            }

            entries.AddRange(line[(open + 1)..close]
                .Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        return entries;
    }

    private static async Task<bool> ReadelfIsAvailableAsync()
    {
        try
        {
            return (await RunAsync("readelf", "--version")).ExitCode == 0;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    private static async Task<string> ReadDynamicSectionAsync(string path)
    {
        var (exitCode, output) = await RunAsync("readelf", "-d", path);
        AssertEx.Equal(expected: 0, exitCode, "readelf must be able to read the copied binary's dynamic section.");
        return output;
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string fileName, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        _ = process.Start();
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, output);
    }
}
