namespace XE_Local_AI_Engine.Providers.ProcessSupervision;

using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.ProcessSupervision.Contracts;

/// <summary>
///     One file per live runtime child under THIS node's data directory, <c>runtime/&lt;server&gt;/&lt;pid&gt;.json</c>, so a restart
///     after a hard host kill can reap exactly the orphans this node spawned, wherever their binary lives.
/// </summary>
/// <remarks>
///     Written after the handle exists and removed when the tracked handle is disposed, which every supervisor teardown path does; a
///     receipt left on disk therefore means the host died without tearing its child down. Linux only: the identity is the
///     <c>/proc</c> start time, which macOS has no cheap equivalent for, and Windows children die with their Job Object anyway. Off
///     Linux, or with no data directory, the store is disabled and <see cref="TrackAsync" /> returns the handle unchanged.
/// </remarks>
public sealed class ProcessSpawnReceiptStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly string? _directory;
    private readonly ILogger _logger;

    /// <summary>Receipts for <paramref name="serverName" /> children under <paramref name="nodeDataRoot" />; a blank root disables the store.</summary>
    public ProcessSpawnReceiptStore(string? nodeDataRoot, string serverName, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _directory = OperatingSystem.IsLinux() && !string.IsNullOrWhiteSpace(nodeDataRoot)
            ? Path.Combine(Path.GetFullPath(nodeDataRoot), "runtime", serverName)
            : null;
    }

    /// <summary>False off Linux or without a data directory: nothing is written and nothing is read back.</summary>
    public bool IsEnabled => _directory is not null;

    /// <summary>
    ///     Records the child behind <paramref name="handle" /> and returns a handle whose dispose also removes the receipt. Best-effort:
    ///     a receipt that cannot be written is logged and the untracked handle returned, so a spawn never fails over bookkeeping.
    /// </summary>
    /// <remarks>
    ///     <paramref name="executablePath" /> is the binary the launcher started, canonicalized here, not read from <c>/proc</c>: a child
    ///     started under <c>setsid</c> has not necessarily exec'd yet, so its <c>/proc/[pid]/exe</c> may still name the wrapper.
    /// </remarks>
    public async Task<IProcessTreeHandle> TrackAsync(IProcessTreeHandle handle, string executablePath, string label, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(label);
        if (_directory is null)
        {
            return handle;
        }

        var pid = handle.ProcessId;
        if (LinuxProcFs.TryReadStat(pid) is not { } stat || LinuxProcFs.TryRealPath(executablePath) is not { } canonicalPath)
        {
            _logger.LogWarning("No spawn receipt was written for {Label} (pid {Pid}): its identity could not be read; only the managed-root reap covers it.",
                label, pid);
            return handle;
        }

        var receipt = new ProcessSpawnReceipt
        {
            Pid = pid,
            StartTicks = stat.StartTicks,
            ExecutablePath = canonicalPath,
            Label = label
        };
        var path = Path.Combine(_directory, string.Create(CultureInfo.InvariantCulture, $"{pid}.json"));
        try
        {
            _ = Directory.CreateDirectory(_directory);
            await SecureFilePermissions.WriteAllBytesAtomicAsync(path, JsonSerializer.SerializeToUtf8Bytes(receipt, SerializerOptions), cancellationToken)
                                       .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "No spawn receipt was written for {Label} (pid {Pid}); only the managed-root reap covers it.", label, pid);
            return handle;
        }

        return new ReceiptedProcessTreeHandle(handle, path, this);
    }

    /// <summary>Every receipt on disk, keyed by its file; an unparseable one reads as <see langword="null" />.</summary>
    internal IReadOnlyList<KeyValuePair<string, ProcessSpawnReceipt?>> ReadAll()
    {
        var receipts = new List<KeyValuePair<string, ProcessSpawnReceipt?>>();
        if (_directory is null || !Directory.Exists(_directory))
        {
            return receipts;
        }

        foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
        {
            receipts.Add(new KeyValuePair<string, ProcessSpawnReceipt?>(file, TryRead(file)));
        }

        return receipts;
    }

    /// <summary>Removes one receipt; a file that is already gone or cannot be removed is not an error.</summary>
    internal void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(exception, "A spawn receipt could not be removed; the next startup re-validates it.");
        }
    }

    private static ProcessSpawnReceipt? TryRead(string file)
    {
        try
        {
            // Forced sync: read from the synchronous startup reaper; a receipt is a few hundred bytes on local disk.
#pragma warning disable MA0045
            return JsonSerializer.Deserialize<ProcessSpawnReceipt>(File.ReadAllBytes(file), SerializerOptions);
#pragma warning restore MA0045
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Delegates to the launched handle and removes the receipt once the child has been torn down.</summary>
    private sealed class ReceiptedProcessTreeHandle : IProcessTreeHandle
    {
        private readonly IProcessTreeHandle _inner;
        private readonly string _receiptPath;
        private readonly ProcessSpawnReceiptStore _store;

        public ReceiptedProcessTreeHandle(IProcessTreeHandle inner, string receiptPath, ProcessSpawnReceiptStore store)
        {
            _inner = inner;
            _receiptPath = receiptPath;
            _store = store;
        }

        public int ProcessId => _inner.ProcessId;

        public bool HasExited => _inner.HasExited;

        public int? ExitCode => _inner.ExitCode;

        public string? StderrTail => _inner.StderrTail;

        public Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken ct) =>
            _inner.WaitForExitAsync(timeout, ct);

        public void TreeKill() =>
            _inner.TreeKill();

        // After the inner dispose, never before: a host that dies between the two leaves the receipt, and the next start re-validates it.
        public void Dispose()
        {
            try
            {
                _inner.Dispose();
            }
            finally
            {
                _store.Delete(_receiptPath);
            }
        }
    }
}
