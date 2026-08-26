namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

/// <summary>Controls whether streamed child-process output is still part of the startup diagnostic window.</summary>
internal sealed class LlamaServerDiagnosticVerbosityWindow
{
    private volatile bool _serving;

    public Func<bool> IsServing => () => _serving;

    public void MarkServing()
    {
        _serving = true;
    }
}

/// <summary>Captures the first complete llama.cpp layer-placement banner from concurrent output streams.</summary>
internal sealed class LlamaServerLayerPlacementSniffer
{
    private readonly Lock _gate = new();
    private int _offloaded;
    private volatile int _total;

    public void Add(string line)
    {
        if (_total > 0 || !LlamaLayerOffloadBanner.TryParse(line, out var offloaded, out var total))
        {
            return;
        }

        lock (_gate)
        {
            if (_total > 0)
            {
                return;
            }

            _offloaded = offloaded;
            _total = total;
        }
    }

    public bool TryGetObservation(out int offloaded, out int total)
    {
        lock (_gate)
        {
            offloaded = _offloaded;
            total = _total;
            return total > 0;
        }
    }
}

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
