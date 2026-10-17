namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Reaping;

using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

/// <summary>
///     The <c>owner.json</c> in a <see cref="ProcessSandboxRuntimeProvider" /> session root: which engine process owns the folder.
/// </summary>
/// <remarks>
///     Markers alone cannot do it: they are written only for a process-group or scope-unit launch, so Windows writes none, and on both
///     platforms the session root itself is removed only by <c>Dispose</c>, which a killed engine never runs (N-64).
/// </remarks>
internal sealed class SandboxSessionOwner
{
    /// <summary>The file name inside a session root.</summary>
    public const string FileName = "owner.json";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public required int ProcessId { get; init; }

    public required DateTime ProcessStartTimeUtc { get; init; }

    /// <summary>The engine build that wrote it; informational only, never compared.</summary>
    public string? EngineVersion { get; init; }

    /// <summary>The record for the current process.</summary>
    public static SandboxSessionOwner ForCurrentProcess()
    {
        using var current = Process.GetCurrentProcess();
        return new SandboxSessionOwner
        {
            ProcessId = Environment.ProcessId,
            ProcessStartTimeUtc = current.StartTime.ToUniversalTime(),
            EngineVersion = typeof(SandboxSessionOwner).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        };
    }

    public void WriteTo(string sessionRoot)
    {
#pragma warning disable MA0045 // forced sync: written from the provider's constructor
        File.WriteAllText(Path.Combine(sessionRoot, FileName), JsonSerializer.Serialize(this, SerializerOptions));
#pragma warning restore MA0045
    }

    /// <summary>The record in <paramref name="sessionRoot" />; null when there is none or it cannot be read as one.</summary>
    public static SandboxSessionOwner? TryReadFrom(string sessionRoot)
    {
        var path = Path.Combine(sessionRoot, FileName);
        try
        {
#pragma warning disable MA0045 // forced sync: read by the reaper's synchronous sweep of a few small files at startup
            return File.Exists(path) ? JsonSerializer.Deserialize<SandboxSessionOwner>(File.ReadAllText(path), SerializerOptions) : null;
#pragma warning restore MA0045
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>True while a process with this id runs and started when the record says, within a tolerance for clock rounding.</summary>
    /// <remarks>An unreadable start time counts as alive: deleting a live engine's sandboxes is the mistake that cannot be undone.</remarks>
    public static bool IsAlive(int processId, DateTime processStartTimeUtc)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return (process.StartTime.ToUniversalTime() - processStartTimeUtc).Duration() <= TimeSpan.FromSeconds(2);
        }
        catch (ArgumentException)
        {
            // No process with that id runs.
            return false;
        }
        catch (InvalidOperationException)
        {
            // It exited between the lookup and the read.
            return false;
        }
        catch (Exception exception) when (exception is Win32Exception or NotSupportedException)
        {
            return true;
        }
    }
}
