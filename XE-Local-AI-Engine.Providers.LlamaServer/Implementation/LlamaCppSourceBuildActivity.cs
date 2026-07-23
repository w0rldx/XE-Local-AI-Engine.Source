namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>Lock-protected process-wide source-build reservation.</summary>
internal sealed class LlamaCppSourceBuildActivity : ILlamaCppSourceBuildActivity
{
    private readonly Lock _gate = new();
    private Guid? _activeBuildId;

    public Guid? ActiveBuildId
    {
        get
        {
            lock (_gate)
            {
                return _activeBuildId;
            }
        }
    }

    public bool TryReserve(Guid buildId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(buildId, Guid.Empty);
        lock (_gate)
        {
            if (_activeBuildId is not null)
            {
                return false;
            }

            _activeBuildId = buildId;
            return true;
        }
    }

    public bool TryRelease(Guid buildId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(buildId, Guid.Empty);
        lock (_gate)
        {
            if (_activeBuildId != buildId)
            {
                return false;
            }

            _activeBuildId = null;
            return true;
        }
    }
}
