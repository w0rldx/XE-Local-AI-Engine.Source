namespace XE_Local_AI_Engine.Tests.Providers.ProcessSupervision;

using System.Buffers;
using System.ComponentModel;
using System.IO.Pipelines;
using System.Runtime.InteropServices;
using XE_Local_AI_Engine.Providers.ProcessSupervision;

/// <summary>
///     A recording <see cref="IWindowsJobApi" />: every handle it issues counts its own release, any step can be made to fail, and a
///     created process exits with whatever <see cref="ExitCodeFor" /> says, or only when its job is terminated.
/// </summary>
internal sealed class FakeWindowsJobApi : IWindowsJobApi
{
    private readonly List<FakeHandle> _handles = [];
    private readonly Dictionary<SafeHandle, TaskCompletionSource> _exits = [];
    private readonly Dictionary<SafeHandle, int> _exitCodes = [];
    private readonly Dictionary<SafeHandle, List<SafeHandle>> _members = [];
    private readonly Dictionary<SafeHandle, FakePipe> _pendingOutputs = [];
    private WindowsJobLimits _set;

    public List<string> Calls { get; } = [];

    public List<WindowsJobStartInfo> Started { get; } = [];

    public int Processors { get; set; } = 16;

    /// <summary>The step that throws a <see cref="Win32Exception" />, by its <see cref="Calls" /> name.</summary>
    public string? FailAt { get; set; }

    public bool Member { get; set; } = true;

    /// <summary>Replaces the read-back; null reads back exactly what was set.</summary>
    public Func<WindowsJobLimits, WindowsJobLimits>? ReadBack { get; set; }

    /// <summary>The exit code of a created process, or null for one that runs until its job is terminated.</summary>
    public Func<WindowsJobStartInfo, int?> ExitCodeFor { get; set; } = static _ => 0;

    /// <summary>Answers a commit of the given bytes with the Win32 error it fails with, or 0 when it succeeds.</summary>
    public Func<ulong, int> Commit { get; set; } = static _ => 0;

    /// <summary>What a created process writes to stdout before it exits.</summary>
    public string StandardOutputText { get; set; } = string.Empty;

    public FakePipe? LastStandardInput { get; private set; }

    public int OpenHandles => _handles.Count(static handle => !handle.IsClosed);

    public int IssuedHandles => _handles.Count;

    public int Terminations { get; private set; }

    /// <summary>The limits the last job was configured with, the CPU rate included.</summary>
    public WindowsJobLimits LastSetLimits => _set;

    public int GetSystemProcessorCount() => Processors;

    public SafeHandle CreateJob()
    {
        Step(nameof(CreateJob));
        var job = Issue("job");
        _members[job] = [];
        return job;
    }

    public void SetExtendedLimits(SafeHandle job, WindowsJobLimits limits)
    {
        Step(nameof(SetExtendedLimits));
        _set = limits with
        {
            CpuRate = 0
        };
    }

    public void SetCpuRate(SafeHandle job, uint cpuRate)
    {
        Step(nameof(SetCpuRate));
        _set = _set with
        {
            CpuRate = cpuRate
        };
    }

    public WindowsJobLimits QueryLimits(SafeHandle job)
    {
        Step(nameof(QueryLimits));
        return ReadBack is null ? _set : ReadBack(_set);
    }

    public WindowsJobChild CreateSuspended(SafeHandle job, WindowsJobStartInfo startInfo)
    {
        Step(nameof(CreateSuspended));
        Started.Add(startInfo);
        var process = Issue("process");
        var output = new FakePipe();
        output.Write(StandardOutputText);
        LastStandardInput = new FakePipe();
        _exits[process] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _members[job].Add(process);
        if (ExitCodeFor(startInfo) is { } exitCode)
        {
            _exitCodes[process] = exitCode;
            output.Complete();
        }
        else
        {
            _pendingOutputs[process] = output;
        }

        var error = new FakePipe();
        error.Complete();
        return new WindowsJobChild
        {
            Process = process,
            Thread = Issue("thread"),
            ProcessId = 4242,
            StandardInput = LastStandardInput.Writer,
            StandardOutput = output.Reader,
            StandardError = error.Reader
        };
    }

    public bool IsProcessInJob(SafeHandle process, SafeHandle job)
    {
        Step(nameof(IsProcessInJob));
        return Member;
    }

    public void Resume(SafeHandle thread)
    {
        Step(nameof(Resume));
        // A resumed process with a known exit code runs to it at once.
        foreach (var (process, exitCode) in _exitCodes)
        {
            _ = exitCode;
            _exits[process].TrySetResult();
        }
    }

    public void Terminate(SafeHandle job)
    {
        Calls.Add(nameof(Terminate));
        ObjectDisposedException.ThrowIf(job.IsClosed, job);
        Terminations++;
        foreach (var process in _members[job])
        {
            _exitCodes.TryAdd(process, 1);
            if (_pendingOutputs.Remove(process, out var output))
            {
                output.Complete();
            }

            _exits[process].TrySetResult();
        }
    }

    public Task WaitForExitAsync(SafeHandle process)
    {
        Step(nameof(WaitForExitAsync));
        return _exits[process].Task;
    }

    public int GetExitCode(SafeHandle process) => _exitCodes[process];

    public void CancelPendingReads(Stream stream) => Calls.Add(nameof(CancelPendingReads));

    public bool TryCommit(SafeHandle process, ulong bytes, out int error)
    {
        Calls.Add(nameof(TryCommit));
        error = Commit(bytes);
        return error == 0;
    }

    private void Step(string name)
    {
        Calls.Add(name);
        if (string.Equals(FailAt, name, StringComparison.Ordinal))
        {
            throw new Win32Exception(5, $"injected failure at {name}");
        }
    }

    private FakeHandle Issue(string kind)
    {
        var handle = new FakeHandle(kind);
        _handles.Add(handle);
        return handle;
    }

    internal sealed class FakeHandle : SafeHandle
    {
        public FakeHandle(string kind) : base(IntPtr.Zero, ownsHandle: true)
        {
            Kind = kind;
            SetHandle(1);
        }

        public string Kind { get; }

        public override bool IsInvalid => false;

        protected override bool ReleaseHandle() => true;
    }

    /// <summary>An in-memory pipe whose read end reaches end of stream only once the writer completes.</summary>
    internal sealed class FakePipe
    {
        private readonly Pipe _pipe = new();

        public FakePipe()
        {
            Reader = _pipe.Reader.AsStream();
            Writer = _pipe.Writer.AsStream();
        }

        public Stream Reader { get; }

        public Stream Writer { get; }

        // Committed by Complete: a fake process's output is readable once it has exited.
        public void Write(string text) =>
            _pipe.Writer.Write(System.Text.Encoding.UTF8.GetBytes(text));

        public void Complete() => _pipe.Writer.Complete();
    }
}
