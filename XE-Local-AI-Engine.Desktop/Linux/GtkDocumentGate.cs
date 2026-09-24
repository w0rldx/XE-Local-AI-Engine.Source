namespace XE_Local_AI_Engine.Desktop.Linux;

internal sealed class GtkDocumentGate
{
    private readonly Lock _sync = new();
    private long _generation;
    private bool _armed;
    private bool _closed;

    internal long Invalidate()
    {
        lock (_sync)
        {
            _armed = false;
            return ++_generation;
        }
    }

    internal bool Arm(long generation)
    {
        lock (_sync)
        {
            if (_closed || generation != _generation)
            {
                return false;
            }

            _armed = true;
            return true;
        }
    }

    internal long Generation
    {
        get
        {
            lock (_sync) { return _generation; }
        }
    }

    internal bool IsCurrent(long generation)
    {
        lock (_sync) { return !_closed && _armed && generation == _generation; }
    }

    internal bool ExecuteIfCurrent(long generation, Action action)
    {
        lock (_sync)
        {
            if (_closed || !_armed || generation != _generation)
            {
                return false;
            }

            action();
            return true;
        }
    }

    internal void Close()
    {
        lock (_sync)
        {
            _closed = true;
            _armed = false;
            _generation++;
        }
    }
}
