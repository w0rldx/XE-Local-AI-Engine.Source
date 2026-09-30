namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

/// <summary>Retains a bounded tail of startup output for deterministic failure classification.</summary>
internal sealed class LlamaServerBoundedStartupCapture
{
    private const int MaximumCharacters = 16 * 1024;
    private const int MaximumLines = 64;
    private readonly Lock _gate = new();
    private readonly Queue<string> _lines = new();
    private int _characters;

    public void Add(string line)
    {
        var captured = line.Length <= MaximumCharacters ? line : line[..MaximumCharacters];
        lock (_gate)
        {
            _lines.Enqueue(captured);
            _characters += captured.Length;
            while (_lines.Count > MaximumLines || (_characters > MaximumCharacters && _lines.Count > 1))
            {
                _characters -= _lines.Dequeue().Length;
            }
        }
    }

    public IReadOnlyList<string> Snapshot()
    {
        lock (_gate)
        {
            return [.. _lines];
        }
    }
}
