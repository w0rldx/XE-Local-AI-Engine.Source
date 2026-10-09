namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

/// <summary>Captures the first complete llama.cpp layer-placement banner from concurrent output streams.</summary>
/// <remarks>
///     llama.cpp prints the banner from the requested layer count even when no GPU backend loaded (a CUDA build missing
///     its companion DLLs falls back to the CPU and still says "offloaded 41/41 layers to GPU"). The model buffer lines
///     name the device that really holds the weights, so a load whose buffers are all host buffers reports 0 offloaded.
/// </remarks>
internal sealed class LlamaServerLayerPlacementSniffer
{
    private readonly Lock _gate = new();
    private int _offloaded;
    private int _total;
    private bool _sawModelBuffer;
    private bool _sawGpuModelBuffer;

    public void Add(string line)
    {
        if (LlamaLayerOffloadBanner.TryParseModelBufferDevice(line, out var device))
        {
            var isGpu = !LlamaLayerOffloadBanner.IsHostBuffer(device);
            lock (_gate)
            {
                _sawModelBuffer = true;
                _sawGpuModelBuffer |= isGpu;
            }

            return;
        }

        if (!LlamaLayerOffloadBanner.TryParse(line, out var offloaded, out var total))
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
            offloaded = _sawModelBuffer && !_sawGpuModelBuffer ? 0 : _offloaded;
            total = _total;
            return total > 0;
        }
    }
}
