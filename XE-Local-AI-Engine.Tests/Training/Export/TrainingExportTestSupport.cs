namespace XE_Local_AI_Engine.Tests.Training.Export;

using System.Runtime.CompilerServices;
using XE_Local_AI_Engine.Providers.Training.Contracts;

/// <summary>
///     A spawner that answers each spawn in order with a scripted side effect. The export pipeline runs three
///     subprocesses back to back, so the run-suite's single-handle fake cannot express it.
/// </summary>
internal sealed class ScriptedExportSpawner : ITrainingProcessSpawner
{
    private readonly Queue<ScriptedSpawn> _script = new();

    public List<TrainingSpawnRequest> Requests { get; } = [];

    public ScriptedExportSpawner Then(int exitCode = 0, Action<TrainingSpawnRequest>? effect = null, params string[] lines)
    {
        _script.Enqueue(new ScriptedSpawn { ExitCode = exitCode, Effect = effect, Lines = lines });
        return this;
    }

    public ITrainingProcessHandle Spawn(TrainingSpawnRequest request)
    {
        Requests.Add(request);
        if (!_script.TryDequeue(out var scripted))
        {
            throw new InvalidOperationException($"Unscripted spawn of '{request.ExecutablePath}'.");
        }

        scripted.Effect?.Invoke(request);
        return new ScriptedHandle(scripted.ExitCode, scripted.Lines);
    }

    private sealed record ScriptedSpawn
    {
        public required int ExitCode { get; init; }

        public required Action<TrainingSpawnRequest>? Effect { get; init; }

        public required IReadOnlyList<string> Lines { get; init; }
    }

    private sealed class ScriptedHandle : ITrainingProcessHandle
    {
        private readonly int _exitCode;
        private readonly IReadOnlyList<string> _lines;

        public ScriptedHandle(int exitCode, IReadOnlyList<string> lines)
        {
            _exitCode = exitCode;
            _lines = lines;
        }

        public TrainingLaunchReceipt Receipt { get; } = new() { Pid = 1, Pgid = 1, ExecutablePath = "/venv/bin/python", StartTicks = 1, RunToken = "token" };

        public IAsyncEnumerable<string> ReadOutputAsync(CancellationToken cancellationToken) =>
            Emit(cancellationToken);

        public Task<int> WaitForExitAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_exitCode);

        public void RequestStop()
        {
        }

        public void KillGroup()
        {
        }

        public void Dispose()
        {
        }

        private async IAsyncEnumerable<string> Emit([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var line in _lines)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return line;
            }

            await Task.CompletedTask;
        }
    }
}
