namespace XE_Local_AI_Engine.HostAgent.Windows.Implementation;

using System.Text.Json;

/// <summary>
///     Persistence boundary for desired state data.
/// </summary>
public sealed class DesiredStateStore : IDisposable
{
    public const string Running = "running";
    public const string Stopped = "stopped";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim _lock = new(1, 1);

    private readonly HostAgentWindowsPaths _paths;

    public DesiredStateStore(HostAgentWindowsPaths paths)
    {
        _paths = paths;
    }

    public void Dispose()
    {
        _lock.Dispose();
    }

    public async Task<string> GetDesiredStateAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_paths.DesiredStatePath))
            {
                return Running;
            }

            await using var stream = File.OpenRead(_paths.DesiredStatePath);
            var state = await JsonSerializer.DeserializeAsync<DesiredStateDocument>(stream,
                SerializerOptions,
                cancellationToken).ConfigureAwait(false);

            return NormalizeState(state?.DesiredState);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SetDesiredStateAsync(string desiredState, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeState(desiredState);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_paths.RootDirectory);
            await using var stream = File.Create(_paths.DesiredStatePath);
            await JsonSerializer.SerializeAsync(stream,
                new DesiredStateDocument(normalized),
                SerializerOptions,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public static string NormalizeState(string? desiredState)
    {
        return string.Equals(desiredState, Stopped, StringComparison.OrdinalIgnoreCase)
            ? Stopped
            : Running;
    }

    private sealed record DesiredStateDocument(string DesiredState);
}
