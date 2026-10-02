namespace XE_Local_AI_Engine.Tests.NodeSettings;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.DependencyInjection.Modules;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;
using XE_Local_AI_Engine.Client.Services.ExternalApps;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.NodeSettings.Implementation;
using XE_Local_AI_Engine.Client.Services.Scheduler;
using XE_Local_AI_Engine.Client.Services.Transcription;
using XE_Local_AI_Engine.Client.Services.WorkSessions;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;

/// <summary>
///     Each production module overlays its options' <c>Enabled</c> with the node-settings switch after the bind, so a reader left
///     on the options sees the stored value, and the startup validators judge the overlaid values.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class FeatureSwitchOptionsOverlayTests
{
    [Test]
    [Arguments(null, true)]
    [Arguments(false, false)]
    public void EachOverlaidSwitch_IsTheStoredValue_ElseTheConfigurationSeed(bool? stored, bool expected)
    {
        // Every seed is ON here, so a stored off is the only way a switch reads off.
        using var provider = Compose(Seeds(enabled: true), stored is { } value ? AllStored(value) : new StoredNodeSettings());

        AssertEx.Equal(expected, provider.GetRequiredService<IOptions<DevelopmentOptions>>().Value.Enabled, "Development");
        AssertEx.Equal(expected, provider.GetRequiredService<IOptions<WorkSessionOptions>>().Value.Enabled, "WorkSessions");
        AssertEx.Equal(expected, provider.GetRequiredService<IOptions<GraphWorkflowOptions>>().Value.Enabled, "GraphWorkflows");
        AssertEx.Equal(expected, provider.GetRequiredService<IOptions<TranscriptionOptions>>().Value.Enabled, "Transcription");
        AssertEx.Equal(expected, provider.GetRequiredService<IOptions<ExternalAppsOptions>>().Value.Enabled, "ExternalApps");
        AssertEx.Equal(expected, provider.GetRequiredService<IOptions<SchedulerOptions>>().Value.Enabled, "Scheduler");
        AssertEx.Equal(expected, provider.GetRequiredService<IOptions<DevWorkflowOptions>>().Value.Enabled, "DevWorkflows");
    }

    [Test]
    public void TheDevWorkflowValidator_JudgesTheStoredSwitchesOfBothSections()
    {
        // Seeds that would pass, stored values that must not: the validator has to see the overlaid pair, not the configuration.
        using var refused = Compose(Seeds(enabled: false), new StoredNodeSettings
        {
            DevWorkflowsEnabled = true,
            WorkSessionsEnabled = false
        });
        using var accepted = Compose(Seeds(enabled: false), new StoredNodeSettings
        {
            DevWorkflowsEnabled = true,
            WorkSessionsEnabled = true
        });

        var exception = AssertEx.Throws<OptionsValidationException>(() => _ = refused.GetRequiredService<IOptions<DevWorkflowOptions>>().Value);
        AssertEx.Contains(string.Join(" ", exception.Failures), "WorkSessions");
        AssertEx.True(accepted.GetRequiredService<IOptions<DevWorkflowOptions>>().Value.Enabled);
    }

    private static Dictionary<string, string?> Seeds(bool enabled)
    {
        var value = enabled ? "true" : "false";
        return new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Development:Enabled"] = value,
            ["WorkSessions:Enabled"] = value,
            ["GraphWorkflows:Enabled"] = value,
            ["Transcription:Enabled"] = value,
            ["ExternalApps:Enabled"] = value,
            ["Scheduler:Enabled"] = value,
            ["DevWorkflows:Enabled"] = value
        };
    }

    private static StoredNodeSettings AllStored(bool value) =>
        new()
        {
            DevelopmentEnabled = value,
            WorkSessionsEnabled = value,
            GraphWorkflowsEnabled = value,
            TranscriptionEnabled = value,
            ExternalAppsEnabled = value,
            SchedulerEnabled = value,
            DevWorkflowsEnabled = value
        };

    private static ServiceProvider Compose(Dictionary<string, string?> seeds, StoredNodeSettings stored)
    {
        var builder = Host.CreateEmptyApplicationBuilder(settings: null);
        builder.Configuration.AddInMemoryCollection(seeds);
        builder.Services.AddSingleton(SeededNodeRuntimeSettings.Create(seeds, () => stored));

        // Registration only: nothing below resolves a store, a supervisor or a database, just the options and their validators.
        var startupSettings = new NodeStartupSettings();
        _ = builder.AddNodeDevelopment(builder.Configuration, startupSettings);
        _ = builder.AddNodeWorkSessions(builder.Configuration);
        _ = builder.AddNodeGraphWorkflows(builder.Configuration);
        _ = builder.AddNodeTranscription(builder.Configuration);
        _ = builder.AddNodeExternalApps(builder.Configuration);
        _ = builder.AddNodeModelCapabilitiesAndMcp(builder.Configuration);
        _ = builder.AddNodeDevWorkflows(builder.Configuration, startupSettings);
        return builder.Services.BuildServiceProvider();
    }
}
