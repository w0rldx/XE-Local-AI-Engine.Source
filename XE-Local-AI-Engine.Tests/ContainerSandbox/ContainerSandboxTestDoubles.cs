namespace XE_Local_AI_Engine.Tests.ContainerSandbox;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>An <see cref="IOptionsMonitor{TOptions}" /> over a fixed value.</summary>
internal sealed class StaticOptionsMonitor<TOptions> : IOptionsMonitor<TOptions>
{
    public StaticOptionsMonitor(TOptions value)
    {
        CurrentValue = value;
    }

    public TOptions CurrentValue { get; }

    public TOptions Get(string? name)
    {
        return CurrentValue;
    }

    public IDisposable? OnChange(Action<TOptions, string?> listener)
    {
        return null;
    }
}

/// <summary>A <see cref="TimeProvider" /> pinned to one instant, so timestamps in assertions are exact.</summary>
internal sealed class FixedTimeProvider : TimeProvider
{
    private readonly DateTimeOffset _now;

    public FixedTimeProvider(DateTimeOffset now)
    {
        _now = now;
    }

    public override DateTimeOffset GetUtcNow()
    {
        return _now;
    }
}

/// <summary>A temporary directory tree, deleted on dispose. Shared so a fixture never invents a second one.</summary>
internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xe-docker-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort teardown of a temp tree.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort teardown of a temp tree.
        }
    }
}

/// <summary>An <see cref="INodeDataDirectory" /> rooted at a path the test owns.</summary>
internal sealed class FixedNodeDataDirectory : INodeDataDirectory
{
    public FixedNodeDataDirectory(string root)
    {
        Root = root;
    }

    public string Root { get; }
}
