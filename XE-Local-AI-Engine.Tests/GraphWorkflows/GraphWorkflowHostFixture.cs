namespace XE_Local_AI_Engine.Tests.GraphWorkflows;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TUnit.Core.Interfaces;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;

/// <summary>
///     One graph-workflow host for a whole test class (<c>ClassDataSource(SharedType.PerClass)</c>), so a class pays
///     the host build once instead of once per test.
///     <para>
///         The host is shared, so its SQLite database is too: scope every read to your own run id and assert on no
///         absolute row count. A test that needs host-level state of its own — a different option value — builds its
///         own factory with <see cref="NewFactory" /> and says why at the construction site.
///     </para>
/// </summary>
public sealed class GraphWorkflowHostFixture : IAsyncInitializer, IAsyncDisposable
{
    public TestServerWebAppFactory Factory { get; } = NewFactory();

    /// <summary>
    ///     The graph-workflow host shape: the feature on, and the dispatcher signal replaced by a recorder.
    ///     <para>
    ///         The recorder is the ONLY seam swapped. Nothing else is faked — the store, the parser, the state machine
    ///         and the encrypted columns are all the real thing, which is what makes a concurrency assertion here mean
    ///         anything.
    ///     </para>
    /// </summary>
    public static TestServerWebAppFactory NewFactory(params (string Key, string Value)[] configuration)
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["GraphWorkflows:Enabled"] = "true"
        };
        foreach (var (key, value) in configuration)
        {
            settings[key] = value;
        }

        return new TestServerWebAppFactory
        {
            AdditionalConfiguration = settings,
            ConfigureAdditionalTestServices = static services =>
            {
                services.RemoveAll<IGraphWorkflowDispatcherSignal>();
                services.AddSingleton<IGraphWorkflowDispatcherSignal, RecordingGraphWorkflowDispatcherSignal>();
            }
        };
    }

    public Task InitializeAsync() =>
        Task.CompletedTask;

    public ValueTask DisposeAsync() =>
        Factory.DisposeAsync();
}

/// <summary>
///     The dispatcher signal, recorded rather than dropped. A container singleton, so a shared host's recorder
///     accumulates every sibling's traffic — count only your own run id.
/// </summary>
internal sealed class RecordingGraphWorkflowDispatcherSignal : IGraphWorkflowDispatcherSignal
{
    private readonly Lock _gate = new();
    private readonly List<Guid> _signalled = [];

    public void Signal(Guid runId)
    {
        lock (_gate)
        {
            _signalled.Add(runId);
        }
    }

    public int CountFor(Guid runId)
    {
        lock (_gate)
        {
            return _signalled.Count(signalled => signalled == runId);
        }
    }
}
