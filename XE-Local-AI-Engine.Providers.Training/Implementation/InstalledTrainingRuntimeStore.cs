namespace XE_Local_AI_Engine.Providers.Training.Implementation;

using System.Text.Json;
using XE_Local_AI_Engine.Providers.Training.Contracts;

/// <summary>
///     Reads and writes <c>installed-training-runtime.json</c>, the sibling-to-the-venv state record describing what is
///     currently provisioned. Mirrors <c>InstalledRuntimeStore</c>'s role for llama.cpp: it records what was adopted, it
///     does not perform the adoption.
/// </summary>
/// <remarks>
///     Writes go to a temp sibling and are then moved into place, so a crash mid-write leaves the previous record intact
///     rather than a truncated file that would make an installed runtime look absent. A record that cannot be parsed is
///     treated as absent for the same reason a half-written one is: the only safe reading of an unreadable state file is
///     "nothing is installed", which makes the next install rebuild rather than trust it.
/// </remarks>
internal sealed class InstalledTrainingRuntimeStore(string statePath)
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _statePath = !string.IsNullOrWhiteSpace(statePath)
        ? statePath
        : throw new ArgumentException("The state path is required.", nameof(statePath));

    public async Task<InstalledTrainingRuntimeState?> ReadAsync(CancellationToken ct)
    {
        if (!File.Exists(_statePath))
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(_statePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return await JsonSerializer.DeserializeAsync<InstalledTrainingRuntimeState>(stream, SerializerOptions, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public async Task WriteAsync(InstalledTrainingRuntimeState state, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(state);

        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        var tempPath = $"{_statePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, state, SerializerOptions, ct).ConfigureAwait(false);
            }

            File.Move(tempPath, _statePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    public void Delete()
    {
        try
        {
            if (File.Exists(_statePath))
            {
                File.Delete(_statePath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort: the venv removal is what makes the runtime gone; a stale record is re-validated on read.
        }
    }
}
