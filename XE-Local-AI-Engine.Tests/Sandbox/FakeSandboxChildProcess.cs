namespace XE_Local_AI_Engine.Tests.Sandbox;

using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;

/// <summary>A sandbox child that runs nothing: it records its tree kills into a shared call log and reports a scripted exit.</summary>
internal sealed class FakeSandboxChildProcess : ISandboxChildProcess
{
    public FakeSandboxChildProcess(List<string> calls, int id = 4242)
    {
        Calls = calls;
        Id = id;
    }

    public List<string> Calls { get; }

    public int Id { get; }

    /// <summary>The exit code the child reports; <see langword="null" /> while it "runs".</summary>
    public int? ExitCode { get; set; }

    public int KillCount { get; private set; }

    public Stream StandardInput => Stream.Null;

    public Stream StandardOutput => Stream.Null;

    public bool TimedOut => false;

    public IReadOnlyList<string> Warnings => [];

    public Task WriteStandardInputAndCloseAsync(string text, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task<int> WaitForExitAsync(CancellationToken cancellationToken) =>
        Task.FromResult(ExitCode ?? 0);

    public Task<bool> TryWaitForExitAsync(CancellationToken cancellationToken) =>
        Task.FromResult(ExitCode is not null);

    public bool Kill()
    {
        KillCount++;
        Calls.Add("tree");
        return true;
    }

    public void Dispose()
    {
        Calls.Add("dispose child");
    }
}
