namespace XE_Local_AI_Engine.Tests.Sandbox;

using XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>Records every observed line per stream, thread-safe because stdout and stderr arrive on different pump threads.</summary>
internal sealed class RecordingSandboxOutputObserver : ISandboxOutputObserver
{
    private readonly Lock _sync = new();
    private readonly List<string> _standardOutput = [];
    private readonly List<string> _standardError = [];

    public IReadOnlyList<string> StandardOutput
    {
        get
        {
            lock (_sync)
            {
                return [.. _standardOutput];
            }
        }
    }

    public IReadOnlyList<string> StandardError
    {
        get
        {
            lock (_sync)
            {
                return [.. _standardError];
            }
        }
    }

    public void OnLine(SandboxOutputSource source, string line)
    {
        lock (_sync)
        {
            (source == SandboxOutputSource.StandardError ? _standardError : _standardOutput).Add(line);
        }
    }
}
