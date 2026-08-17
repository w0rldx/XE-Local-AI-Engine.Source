namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Reaping;

using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
///     File-backed <see cref="ISandboxMarkerStore" />: one small JSON document per live process group under
///     <see cref="SandboxPaths.MarkersRoot" />. That root is a sibling of the per-instance jail containers, so a
///     provider deleting its own container root on dispose does not take the markers with it, and the location is stable
///     across restarts — which is the whole point, since a crashed run leaves nothing else to find its children by.
/// </summary>
public sealed class FileSandboxMarkerStore : ISandboxMarkerStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false
    };

    private readonly ILogger<FileSandboxMarkerStore> _logger;
    private readonly string _markersRoot;

    // The logger and root are optional so tests can point the store at an isolated directory; ActivatorUtilities
    // injects the logger in production and the default root is the stable shared one.
    public FileSandboxMarkerStore(ILogger<FileSandboxMarkerStore>? logger = null, string? markersRoot = null)
    {
        _logger = logger ?? NullLogger<FileSandboxMarkerStore>.Instance;
        _markersRoot = markersRoot ?? SandboxPaths.MarkersRoot;
    }

    /// <summary>The directory this store reads and writes. Exposed so the reaper can report it and tests can isolate it.</summary>
    public string MarkersRoot => _markersRoot;

    /// <inheritdoc />
    public string? Write(SandboxProcessMarker marker)
    {
        ArgumentNullException.ThrowIfNull(marker);

        // The pid alone is not unique over time, so the id carries a random suffix; the pid stays in the name purely to
        // make a stray marker readable at a glance during diagnosis.
        var markerId = string.Create(CultureInfo.InvariantCulture,
            $"{marker.ProcessGroupId}-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(_markersRoot);
            File.WriteAllText(Path.Combine(_markersRoot, markerId + ".json"), JsonSerializer.Serialize(marker, SerializerOptions));
            return markerId;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // Marker bookkeeping is a reaping convenience, never a correctness requirement for the command in flight.
            _logger.LogDebug(exception, "Could not write the sandbox process marker; orphan reaping will not cover this child.");
            return null;
        }
    }

    /// <inheritdoc />
    public void Delete(string markerId)
    {
        if (string.IsNullOrWhiteSpace(markerId))
        {
            return;
        }

        try
        {
            var path = Path.Combine(_markersRoot, markerId + ".json");
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A leftover marker is harmless: the reaper's pid-reuse and liveness checks make a stale entry a no-op.
            _logger.LogDebug(exception, "Could not delete the sandbox process marker {MarkerId}.", markerId);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<SandboxMarkerEntry> ReadAll()
    {
        var markers = new List<SandboxMarkerEntry>();

        try
        {
            if (!Directory.Exists(_markersRoot))
            {
                return markers;
            }

            foreach (var path in Directory.EnumerateFiles(_markersRoot, "*.json"))
            {
                var marker = TryRead(path);
                if (marker is not null)
                {
                    markers.Add(new SandboxMarkerEntry(Path.GetFileNameWithoutExtension(path), marker));
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(exception, "Could not enumerate sandbox process markers.");
        }

        return markers;
    }

    private SandboxProcessMarker? TryRead(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<SandboxProcessMarker>(File.ReadAllText(path), SerializerOptions);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // A truncated marker (the crash could have interrupted the write) is skipped, not fatal.
            _logger.LogDebug(exception, "Skipping an unreadable sandbox process marker at {Path}.", path);
            return null;
        }
    }
}
