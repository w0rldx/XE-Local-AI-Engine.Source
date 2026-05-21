namespace XE_Local_AI_Engine.Tray;

internal sealed class TraySingleInstanceLock : IDisposable
{
    private readonly FileStream? _lockFile;
    private readonly Mutex? _mutex;
    private bool _disposed;

    private TraySingleInstanceLock(Mutex mutex)
    {
        _mutex = mutex;
    }

    private TraySingleInstanceLock(FileStream lockFile)
    {
        _lockFile = lockFile;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _lockFile?.Dispose();
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        _disposed = true;
    }

    public static TraySingleInstanceLock? TryAcquire(string name)
    {
        return OperatingSystem.IsWindows()
            ? TryAcquireWindowsMutex(name)
            : TryAcquireFileLock(name);
    }

    private static TraySingleInstanceLock? TryAcquireWindowsMutex(string name)
    {
        var mutex = new Mutex(true, $"Local\\{name}", out var createdNew);
        if (createdNew)
        {
            return new TraySingleInstanceLock(mutex);
        }

        mutex.Dispose();
        return null;
    }

    private static TraySingleInstanceLock? TryAcquireFileLock(string name)
    {
        var runtimeDirectory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (string.IsNullOrWhiteSpace(runtimeDirectory))
        {
            runtimeDirectory = Path.GetTempPath();
        }

        var lockDirectory = Path.Combine(runtimeDirectory, "xe-local-ai-engine");
        Directory.CreateDirectory(lockDirectory);

        var lockPath = Path.Combine(lockDirectory, $"{name}.lock");
        try
        {
            var lockFile = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return new TraySingleInstanceLock(lockFile);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
