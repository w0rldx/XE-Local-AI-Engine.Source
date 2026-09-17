namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Work one launch REQUEST does once and every cell of it would otherwise repeat: the llama-server capability
///     probe, the variant it settles on, and the verified installed-model lease per model name. A batch of ten cells
///     used to run ten probes and ten full re-verifications of the same files, serially, before the endpoint answered.
///     <para>
///         Holding the leases for the request's lifetime is deliberate, not just cheaper: a model that changed halfway
///         through a matrix would give the later cells a different snapshot from the earlier ones, which is exactly the
///         variable a matrix exists to hold still. One probe for the same reason — asking twice can straddle a runtime
///         swap and freeze two different answers into one batch.
///     </para>
///     <para>
///         Not thread-safe: one scope belongs to one request, which processes its cells in order.
///     </para>
/// </summary>
public sealed class BenchmarkFreezeScope : IAsyncDisposable
{
    private readonly Dictionary<string, IBenchmarkInstalledModelLease> _leases = new(StringComparer.OrdinalIgnoreCase);
    private bool _inspected;
    private LlamaServerLaunchCapabilities? _capabilities;
    private GpuVariant? _variant;

    /// <summary>How many times the binary was actually inspected. Test-only seam.</summary>
    internal int Inspections { get; private set; }

    /// <summary>How many models were actually verified. Test-only seam.</summary>
    internal int Verifications { get; private set; }

    public async ValueTask DisposeAsync()
    {
        foreach (var lease in _leases.Values.Reverse())
        {
            await lease.DisposeAsync();
        }

        _leases.Clear();
    }

    internal async Task<(LlamaServerLaunchCapabilities? Capabilities, GpuVariant Variant)> InspectAsync(IBenchmarkPhaseLaunchResolver resolver,
        CancellationToken cancellationToken)
    {
        if (!_inspected)
        {
            _capabilities = await resolver.InspectAsync(cancellationToken);
            _variant = await resolver.SelectVariantAsync(_capabilities, cancellationToken);
            _inspected = true;
            Inspections++;
        }

        return (_capabilities, _variant!.Value);
    }

    internal async Task<IBenchmarkInstalledModelLease> AcquireAsync(string modelName,
        Func<string, CancellationToken, Task<IBenchmarkInstalledModelLease>> acquire,
        CancellationToken cancellationToken)
    {
        if (_leases.TryGetValue(modelName, out var cached))
        {
            return cached;
        }

        var lease = await acquire(modelName, cancellationToken);
        _leases[modelName] = lease;
        Verifications++;
        return lease;
    }
}
