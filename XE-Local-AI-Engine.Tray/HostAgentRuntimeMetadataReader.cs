namespace XE_Local_AI_Engine.Tray;

using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

internal static class HostAgentRuntimeMetadataReader
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static HostAgentRuntimeMetadata? TryRead()
    {
        foreach (var path in EnumerateCandidatePaths())
        {
            var metadata = TryRead(path);
            if (metadata is not null)
            {
                return metadata;
            }
        }

        return null;
    }

    private static HostAgentRuntimeMetadata? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var json = File.ReadAllText(path);
            var metadata = JsonSerializer.Deserialize<HostAgentRuntimeMetadata>(json, SerializerOptions);
            if (metadata is null || !IsValid(metadata))
            {
                DeleteStaleMetadata(path);
                return null;
            }

            return metadata;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IEnumerable<string> EnumerateCandidatePaths()
    {
        if (OperatingSystem.IsWindows())
        {
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (!string.IsNullOrWhiteSpace(programData))
            {
                yield return Path.Combine(programData, "XE-Local-AI-Engine", "host-agent", "runtime.json");
            }
        }

        var xdgRuntimeDirectory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (!string.IsNullOrWhiteSpace(xdgRuntimeDirectory))
        {
            yield return Path.Combine(xdgRuntimeDirectory, "xe-host-agent", "runtime.json");
        }
    }

    private static bool IsValid(HostAgentRuntimeMetadata metadata)
    {
        if (metadata.Pid <= 0
            || metadata.AdminPort is <= 0 or > 65535
            || string.IsNullOrWhiteSpace(metadata.ExePath)
            || string.IsNullOrWhiteSpace(metadata.ExeSha256)
            || !File.Exists(metadata.ExePath))
        {
            return false;
        }

        if (!ProcessPathMatches(metadata.Pid, metadata.ExePath))
        {
            return false;
        }

        return string.Equals(ComputeSha256(metadata.ExePath), metadata.ExeSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ProcessPathMatches(int pid, string expectedPath)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited)
            {
                return false;
            }

            var actualPath = ResolveProcessPath(process, pid);
            return !string.IsNullOrWhiteSpace(actualPath)
                   && string.Equals(Path.GetFullPath(actualPath), Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    private static string? ResolveProcessPath(Process process, int pid)
    {
        if (OperatingSystem.IsLinux())
        {
            var procExePath = $"/proc/{pid}/exe";
            return File.Exists(procExePath)
                ? File.ResolveLinkTarget(procExePath, true)?.FullName
                : null;
        }

        return process.MainModule?.FileName;
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void DeleteStaleMetadata(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best-effort stale metadata cleanup; another process may own or remove the file.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort stale metadata cleanup; leave unreadable files untouched.
        }
    }
}
