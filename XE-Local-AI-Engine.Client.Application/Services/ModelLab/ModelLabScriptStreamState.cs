namespace XE_Local_AI_Engine.Client.Services.ModelLab;

/// <summary>What a Model Lab script's output said so far: the frames that end a job and the last lines of plain output.</summary>
internal sealed class ModelLabScriptStreamState
{
    private const int OutputLinesKept = 12;
    private const int OutputLineLength = 300;

    // A native tool's own explanation reaches only stderr: a failed compile says why here and nowhere else.
    private readonly Queue<string> _lastOutput = new();

    public bool HandshakeSeen { get; set; }

    public bool Done { get; set; }

    public bool ScriptCancelled { get; set; }

    public string? ErrorMessage { get; set; }

    /// <summary>The error event's <c>category</c>: the stable code a source job's refusal is mapped from.</summary>
    public string? ErrorCategory { get; set; }

    public void KeepOutput(string line)
    {
        _lastOutput.Enqueue(line.Length > OutputLineLength ? line[..OutputLineLength] : line);
        if (_lastOutput.Count > OutputLinesKept)
        {
            _ = _lastOutput.Dequeue();
        }
    }

    public string WithLastOutput(string message) =>
        _lastOutput.Count == 0 ? message : $"{message}\n\nLast output:\n{string.Join('\n', _lastOutput)}";
}
