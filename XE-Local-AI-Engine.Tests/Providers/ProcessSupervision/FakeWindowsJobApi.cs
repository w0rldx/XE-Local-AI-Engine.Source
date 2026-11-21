namespace XE_Local_AI_Engine.Tests.Providers.ProcessSupervision;

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
    private readonly Dictionary<SafeHandle, SafeHandle> _ports = [];
    private readonly Dictionary<SafeHandle, List<uint>> _queued = [];
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

    /// <summary>When set, replaces <see cref="ExitCodeFor" /> with an answer that also sees the job's active-process cap.</summary>
    public Func<WindowsJobStartInfo, uint, int?>? ExitCodeUnderCap { get; set; }

    /// <summary>Whether a created process hits its job's active-process cap: a job with a port then queues the limit message on it.</summary>
    public Func<WindowsJobStartInfo, uint, bool> HitsActiveProcessLimit { get; set; } = static (_, _) => false;

    /// <summary>Runs before every recorded step, by its <see cref="Calls" /> name: a test blocks a native step here.</summary>
    public Action<string>? BeforeStep { get; set; }

    /// <summary>Answers a commit of the given bytes under the last job's memory limit with the Win32 error it fails with, or 0.</summary>
    public Func<ulong, ulong, int> Commit { get; set; } = static (_, _) => 0;

    /// <summary>What a created process writes to stdout before it exits.</summary>
    public string StandardOutputText { get; set; } = string.Empty;

    public FakePipe? LastStandardInput { get; private set; }

    public ObservedReader? LastStandardOutput { get; private set; }

    public ObservedReader? LastStandardError { get; private set; }

    /// <summary>A process outside the job keeps the child's stdout open: it never reaches end of stream, not even on terminate.</summary>
    public bool HoldStandardOutput { get; set; }

    /// <summary>Closing the child's stdin throws once, as a broken pipe would.</summary>
    public bool StandardInputFailsOnDispose { get; set; }

    /// <summary>Reading an exited process's exit code throws a <see cref="Win32Exception" />.</summary>
    public bool ExitCodeFails { get; set; }

    /// <summary>When set, a direct terminate never confirms the exit: the process handle is held and this reason returned.</summary>
    public string? UnconfirmedExit { get; set; }

    /// <summary>The process handles a direct terminate took over without a confirmed exit.</summary>
    public List<SafeHandle> HeldProcesses { get; } = [];

    /// <summary>The processes terminated directly, not through their job.</summary>
    public int ProcessTerminations { get; private set; }

    public int OpenHandles => _handles.Count(static handle => !handle.IsClosed);

    public int IssuedHandles => _handles.Count;

    public int Terminations { get; private set; }

    /// <summary>What <see cref="GetEngineScheduling" /> reports as the engine's current affinity and priority class.</summary>
    public WindowsJobScheduling EngineScheduling { get; set; } = new() { AffinityMask = 0xF, SystemAffinityMask = 0xFF, PriorityClass = 0x20 };

    /// <summary>Every affinity mask set on a created process, in order.</summary>
    public List<nuint> SetAffinityMasks { get; } = [];

    /// <summary>Every priority class set on a created process, in order.</summary>
    public List<uint> SetPriorityClasses { get; } = [];

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

    public SafeHandle AssociateCompletionPort(SafeHandle job)
    {
        Step(nameof(AssociateCompletionPort));
        var port = Issue("port");
        _ports[job] = port;
        _queued[port] = [];
        return port;
    }

    public bool ReceivedMessage(SafeHandle port, uint message)
    {
        Step(nameof(ReceivedMessage));
        var received = _queued[port].Contains(message);
        _queued[port].Clear();
        return received;
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
        if (_ports.TryGetValue(job, out var port) && HitsActiveProcessLimit(startInfo, _set.ActiveProcessLimit))
        {
            _queued[port].Add(WindowsJobCeilingsProbe.ActiveProcessLimitMessage);
        }
        var answer = ExitCodeUnderCap is null ? ExitCodeFor(startInfo) : ExitCodeUnderCap(startInfo, _set.ActiveProcessLimit);
        if (answer is { } exitCode)
        {
            _exitCodes[process] = exitCode;
            if (!HoldStandardOutput)
            {
                output.Complete();
            }
        }
        else if (!HoldStandardOutput)
        {
            _pendingOutputs[process] = output;
        }

        var error = new FakePipe();
        error.Complete();
        LastStandardOutput = new ObservedReader(output.Reader);
        LastStandardError = new ObservedReader(error.Reader);
        return new WindowsJobChild
        {
            Process = process,
            Thread = Issue("thread"),
            ProcessId = 4242,
            StandardInput = StandardInputFailsOnDispose ? new FailingDisposeStream() : LastStandardInput.Writer,
            StandardOutput = LastStandardOutput,
            StandardError = LastStandardError
        };
    }

    public bool IsProcessInJob(SafeHandle process, SafeHandle job)
    {
        Step(nameof(IsProcessInJob));
        return Member;
    }

    public WindowsJobScheduling GetEngineScheduling()
    {
        Step(nameof(GetEngineScheduling));
        return EngineScheduling;
    }

    public void SetAffinityMask(SafeHandle process, nuint affinityMask)
    {
        Step(nameof(SetAffinityMask));
        SetAffinityMasks.Add(affinityMask);
    }

    public void SetPriorityClass(SafeHandle process, uint priorityClass)
    {
        Step(nameof(SetPriorityClass));
        SetPriorityClasses.Add(priorityClass);
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
        Step(nameof(Terminate));
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

    public int GetExitCode(SafeHandle process) =>
        ExitCodeFails ? throw new Win32Exception(6, "injected: the exit code could not be read") : _exitCodes[process];

    public string? TerminateAndConfirm(SafeHandle process, int processId)
    {
        Calls.Add(nameof(TerminateAndConfirm));
        ProcessTerminations++;
        _exitCodes.TryAdd(process, 1);
        _exits[process].TrySetResult();
        if (UnconfirmedExit is { } reason)
        {
            HeldProcesses.Add(process);
            return reason;
        }

        process.Dispose();
        return null;
    }

    public bool TryCommit(SafeHandle process, ulong bytes, out int error)
    {
        Calls.Add(nameof(TryCommit));
        error = Commit(bytes, _set.JobMemoryLimit);
        return error == 0;
    }

    private void Step(string name)
    {
        Calls.Add(name);
        BeforeStep?.Invoke(name);
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

    internal sealed class FailingDisposeStream : MemoryStream
    {
        private bool _failed;

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && !_failed)
            {
                _failed = true;
                throw new IOException("injected: the pipe broke");
            }
        }
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

    /// <summary>A pipe's read end that counts the reads started on it.</summary>
    internal sealed class ObservedReader : Stream
    {
        private readonly Stream _inner;
        private int _readsStarted;

        public ObservedReader(Stream inner)
        {
            _inner = inner;
        }

        public int ReadsStarted => Volatile.Read(ref _readsStarted);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _readsStarted);
            return await _inner.ReadAsync(buffer, cancellationToken);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count)
        {
            Interlocked.Increment(ref _readsStarted);
            return _inner.Read(buffer, offset, count);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>An in-memory pipe whose read end reaches end of stream only once the writer completes.</summary>
    internal sealed class FakePipe
    {
        // Never pauses its writer, so a write is readable at once, however large.
        private readonly Pipe _pipe = new(new PipeOptions(pauseWriterThreshold: 0));

        public FakePipe()
        {
            Reader = _pipe.Reader.AsStream();
            Writer = _pipe.Writer.AsStream();
        }

        public Stream Reader { get; }

        public Stream Writer { get; }

        // Written and flushed through the stream at once, so a pump sees output that a held pipe never completes; it cannot pause.
        public void Write(string text) =>
            Writer.Write(System.Text.Encoding.UTF8.GetBytes(text));

        public void Complete() => _pipe.Writer.Complete();
    }
}
