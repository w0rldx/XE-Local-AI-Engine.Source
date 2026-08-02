namespace XE_Local_AI_Engine.Tests.Testing;

using System.Diagnostics;

/// <summary>
///     Creates NTFS directory junctions, and answers whether this host lets the test process create one — by trying it
///     once rather than guessing from the OS.
///     <para>
///         <b>Why junctions and not just symbolic links.</b> A symbolic link on Windows needs
///         <c>SeCreateSymbolicLinkPrivilege</c>, which an ordinary account does not hold unless Developer Mode is on or
///         the process is elevated, so <see cref="SymlinkSupport" /> skips on a stock box — measured on the Windows 11
///         machine this was written for: Developer Mode off, not elevated, <c>New-Item -ItemType SymbolicLink</c> fails
///         with <i>"Administrator privilege required for this operation"</i>. A junction needs NO privilege and
///         succeeds there. Both are reparse points, so both are what
///         <c>WorkspaceFileScanner</c>'s no-follow guard has to reject — which means a
///         junction proves that guard on exactly the hosts where the symbolic-link tests cannot run.
///     </para>
///     <para>
///         Junctions are also the shape a real Windows repository is most likely to contain: they are what
///         <c>mklink /J</c>, <c>robocopy /XJ</c> and most build tooling create, and they need no special setup for a
///         user to have made one by accident.
///     </para>
/// </summary>
internal static class JunctionSupport
{
    private static readonly Lazy<bool> Supported = new(Probe, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>True when this process can create NTFS junctions on this host.</summary>
    public static bool IsSupported => Supported.Value;

    /// <summary>Skips the calling test when junctions cannot be created here. Call it before planting one.</summary>
    public static void EnsureSupported()
    {
        if (!IsSupported)
        {
            Skip.Test("This host does not permit creating NTFS junctions (not Windows, or the volume is not NTFS).");
        }
    }

    /// <summary>
    ///     Creates a directory junction at <paramref name="linkPath" /> pointing at <paramref name="targetPath" />.
    ///     <para>
    ///         Shelled out to <c>cmd /c mklink /J</c> because the BCL exposes no junction creation — <c>CreateSymbolicLink</c>
    ///         makes a symbolic link, which is the privileged kind this helper exists to avoid. Writing the reparse
    ///         buffer directly would mean a <c>DeviceIoControl</c> P/Invoke in the test assembly for one fixture.
    ///     </para>
    /// </summary>
    public static bool TryCreate(string linkPath, string targetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(linkPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(linkPath);
        startInfo.ArgumentList.Add(targetPath);

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            _ = process.StandardOutput.ReadToEnd();
            _ = process.StandardError.ReadToEnd();
            process.WaitForExit(milliseconds: 15000);

            // mklink can report success and still have produced nothing if the target vanished; confirm the reparse
            // point really exists rather than trusting the exit code alone.
            return process.HasExited
                   && process.ExitCode == 0
                   && new DirectoryInfo(linkPath).Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static bool Probe()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var root = Path.Combine(Path.GetTempPath(), $"xe-junction-probe-{Guid.NewGuid():N}");
        try
        {
            _ = Directory.CreateDirectory(Path.Combine(root, "target"));
            return TryCreate(Path.Combine(root, "link"), Path.Combine(root, "target"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Best-effort probe cleanup.
            }
        }
    }
}
