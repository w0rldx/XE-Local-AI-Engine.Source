namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

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
