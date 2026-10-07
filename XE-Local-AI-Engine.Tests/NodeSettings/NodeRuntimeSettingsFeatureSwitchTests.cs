namespace XE_Local_AI_Engine.Tests.NodeSettings;

using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.Compute;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;
using XE_Local_AI_Engine.Client.Services.ExternalApps;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.Scheduler;
using XE_Local_AI_Engine.Client.Services.Transcription;
using XE_Local_AI_Engine.Client.Services.WorkSessions;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;

/// <summary>
///     The ten feature switches resolve stored &gt; configuration seed &gt; the options class's code default, through the async
///     getter, its sync twin and the effective values alike.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class NodeRuntimeSettingsFeatureSwitchTests
{
    private static readonly Switch[] Switches =
    [
        new("Development:Enabled", new DevelopmentOptions().Enabled, static (s, v) => s with
            {
                DevelopmentEnabled = v
            },
            static (r, ct) => r.GetDevelopmentEnabledAsync(ct), static r => r.GetDevelopmentEnabled(), static e => e.DevelopmentEnabled),
        new("WorkSessions:Enabled", new WorkSessionOptions().Enabled, static (s, v) => s with
            {
                WorkSessionsEnabled = v
            },
            static (r, ct) => r.GetWorkSessionsEnabledAsync(ct), static r => r.GetWorkSessionsEnabled(), static e => e.WorkSessionsEnabled),
        new("GraphWorkflows:Enabled", new GraphWorkflowOptions().Enabled, static (s, v) => s with
            {
                GraphWorkflowsEnabled = v
            },
            static (r, ct) => r.GetGraphWorkflowsEnabledAsync(ct), static r => r.GetGraphWorkflowsEnabled(), static e => e.GraphWorkflowsEnabled),
        new("Transcription:Enabled", new TranscriptionOptions().Enabled, static (s, v) => s with
            {
                TranscriptionEnabled = v
            },
            static (r, ct) => r.GetTranscriptionEnabledAsync(ct), static r => r.GetTranscriptionEnabled(), static e => e.TranscriptionEnabled),
        new("ExternalApps:Enabled", new ExternalAppsOptions().Enabled, static (s, v) => s with
            {
                ExternalAppsEnabled = v
            },
            static (r, ct) => r.GetExternalAppsEnabledAsync(ct), static r => r.GetExternalAppsEnabled(), static e => e.ExternalAppsEnabled),
        new("Compute:Enabled", new ComputeOptions().Enabled, static (s, v) => s with
            {
                ComputeEnabled = v
            },
            static (r, ct) => r.GetComputeEnabledAsync(ct), static r => r.GetComputeEnabled(), static e => e.ComputeEnabled),
        new("AgentHome:Enabled", new AgentHomeOptions().Enabled, static (s, v) => s with
            {
                AgentHomeEnabled = v
            },
            static (r, ct) => r.GetAgentHomeEnabledAsync(ct), static r => r.GetAgentHomeEnabled(), static e => e.AgentHomeEnabled),
        new("Scheduler:Enabled", new SchedulerOptions().Enabled, static (s, v) => s with
            {
                SchedulerEnabled = v
            },
            static (r, ct) => r.GetSchedulerEnabledAsync(ct), static r => r.GetSchedulerEnabled(), static e => e.SchedulerEnabled),
        new("DevWorkflows:Enabled", new DevWorkflowOptions().Enabled, static (s, v) => s with
            {
                DevWorkflowsEnabled = v
            },
            static (r, ct) => r.GetDevWorkflowsEnabledAsync(ct), static r => r.GetDevWorkflowsEnabled(), static e => e.DevWorkflowsEnabled),
        // No options class: a security-widening switch whose code default is OFF, stated here rather than borrowed (ADR 0019).
        new("ExecutionPreviews:Enabled", codeDefault: false, static (s, v) => s with
            {
                ExecutionPreviewsEnabled = v
            },
            static (r, ct) => r.GetExecutionPreviewsEnabledAsync(ct), static r => r.GetExecutionPreviewsEnabled(), static e => e.ExecutionPreviewsEnabled)
    ];

    [Test]
    public async Task UnsetAndUnseeded_EachSwitchEqualsItsOptionsDefault()
    {
        var sut = SeededNodeRuntimeSettings.Create();
        var effective = sut.ResolveEffectiveValues(new StoredNodeSettings());

        foreach (var feature in Switches)
        {
            AssertEx.Equal(feature.CodeDefault, await feature.Async(sut, CancellationToken.None), feature.Key);
            AssertEx.Equal(feature.CodeDefault, feature.Sync(sut), feature.Key);
            AssertEx.Equal(feature.CodeDefault, feature.Effective(effective), feature.Key);
        }
    }

    [Test]
    public async Task StoredAbsent_EachSwitchUsesTheConfigurationSeed_NotTheCodeDefault()
    {
        // The seed is the opposite of each code default, so a switch that fell through to the default would read wrong.
        var sut = SeededNodeRuntimeSettings.Create(Switches.ToDictionary(static feature => feature.Key,
            static feature => (string?)(feature.CodeDefault ? "false" : "true"),
            StringComparer.Ordinal));
        var effective = sut.ResolveEffectiveValues(new StoredNodeSettings());

        foreach (var feature in Switches)
        {
            AssertEx.Equal(!feature.CodeDefault, await feature.Async(sut, CancellationToken.None), feature.Key);
            AssertEx.Equal(!feature.CodeDefault, feature.Sync(sut), feature.Key);
            AssertEx.Equal(!feature.CodeDefault, feature.Effective(effective), feature.Key);
        }
    }

    [Test]
    public async Task Stored_EachSwitchWinsOverTheSeed()
    {
        var stored = Switches.Aggregate(new StoredNodeSettings(), static (record, feature) => feature.Store(record, feature.CodeDefault));
        var sut = SeededNodeRuntimeSettings.Create(Switches.ToDictionary(static feature => feature.Key,
            static feature => (string?)(feature.CodeDefault ? "false" : "true"),
            StringComparer.Ordinal), () => stored);
        var effective = sut.ResolveEffectiveValues(stored);

        foreach (var feature in Switches)
        {
            AssertEx.Equal(feature.CodeDefault, await feature.Async(sut, CancellationToken.None), feature.Key);
            AssertEx.Equal(feature.CodeDefault, feature.Sync(sut), feature.Key);
            AssertEx.Equal(feature.CodeDefault, feature.Effective(effective), feature.Key);
        }
    }

    [Test]
    public void TheShippedDefaultsThatDifferFromTheSeed_AreTheOnesTheNullFallThroughProtects()
    {
        // appsettings.json ships these three on while the code default is off: a null stored value must reach the seed, never the default.
        AssertEx.False(new WorkSessionOptions().Enabled);
        AssertEx.False(new ExternalAppsOptions().Enabled);
        AssertEx.False(new AgentHomeOptions().Enabled);
    }

    private sealed class Switch
    {
        public Switch(string key,
            bool codeDefault,
            Func<StoredNodeSettings, bool, StoredNodeSettings> store,
            Func<INodeRuntimeSettings, CancellationToken, Task<bool>> readAsync,
            Func<INodeRuntimeSettings, bool> sync,
            Func<NodeSettingsEffectiveValues, bool> effective)
        {
            Key = key;
            CodeDefault = codeDefault;
            Store = store;
            Async = readAsync;
            Sync = sync;
            Effective = effective;
        }

        public string Key { get; }

        public bool CodeDefault { get; }

        public Func<StoredNodeSettings, bool, StoredNodeSettings> Store { get; }

        public Func<INodeRuntimeSettings, CancellationToken, Task<bool>> Async { get; }

        public Func<INodeRuntimeSettings, bool> Sync { get; }

        public Func<NodeSettingsEffectiveValues, bool> Effective { get; }
    }
}
