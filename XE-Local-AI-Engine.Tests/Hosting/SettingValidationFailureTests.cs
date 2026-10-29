namespace XE_Local_AI_Engine.Tests.Hosting;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Hosting;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Tests.Testing;

// An out-of-range Development:WorkspaceRetention value must not stop the node as a generic crash; startup names the setting.
[Category(TestCategories.Unit)]
public sealed class SettingValidationFailureTests
{
    [Test]
    public void TryFind_RecognizesTheFailureAServiceReadingTheOptionsThrows()
    {
        // The hosted service's constructor reads options.Value before the host runs its startup validators.
        using var provider = OutOfRangeServices().BuildServiceProvider();
        var thrown = AssertEx.Throws<OptionsValidationException>(() => _ = provider.GetRequiredService<IOptions<DevelopmentWorkspaceRetentionOptions>>().Value);

        AssertEx.Succeeded(SettingValidationFailure.TryFind(thrown, out var failures), "A single validation failure is recognized.");
        AssertEx.Equal(typeof(DevelopmentWorkspaceRetentionOptions), failures.Single().OptionsType);
    }

    [Test]
    public void TryFind_RecognizesTheAggregateTheStartupValidatorThrowsForTwoOptions()
    {
        var services = OutOfRangeServices();
        services.AddOptions<AgentHomeRunRetentionOptions>()
                .Configure(static options => options.SweepInterval = TimeSpan.FromSeconds(1))
                .Validate(static options => options.SweepInterval >= TimeSpan.FromMinutes(1), "AgentHome:RunRetention:SweepInterval is below one minute.")
                .ValidateOnStart();
        using var provider = services.BuildServiceProvider();
        var thrown = AssertEx.Throws<AggregateException>(() => provider.GetRequiredService<IStartupValidator>().Validate());

        AssertEx.Succeeded(SettingValidationFailure.TryFind(thrown, out var failures), "An aggregate made only of validation failures is recognized.");
        AssertEx.Equal(expected: 2, failures.Count);
        var names = SettingValidationFailure.OptionsTypeNames(failures);
        AssertEx.Contains(names, nameof(DevelopmentWorkspaceRetentionOptions));
        AssertEx.Contains(names, nameof(AgentHomeRunRetentionOptions));
    }

    [Test]
    public void TryFind_IgnoresEveryOtherFailure()
    {
        var validation = new OptionsValidationException("x", typeof(DevelopmentWorkspaceRetentionOptions), ["bad"]);

        AssertEx.False(SettingValidationFailure.TryFind(new InvalidOperationException("boom"), out _), "An ordinary crash.");
        AssertEx.False(SettingValidationFailure.TryFind(new AggregateException(validation, new InvalidOperationException()), out _),
            "An aggregate that also holds a crash stays a crash.");
        AssertEx.False(SettingValidationFailure.TryFind(new AggregateException(), out _), "An empty aggregate.");
        AssertEx.False(SettingValidationFailure.TryFind(null, out _), "No exception.");
    }

    [Test]
    public void Describe_WritesOneLineThatStartsWithThePrefixAndNamesTheSetting()
    {
        using var provider = OutOfRangeServices().BuildServiceProvider();
        var thrown = AssertEx.Throws<OptionsValidationException>(() => _ = provider.GetRequiredService<IOptions<DevelopmentWorkspaceRetentionOptions>>().Value);
        var multiLine = new OptionsValidationException("x", typeof(DevelopmentWorkspaceRetentionOptions), ["first\nsecond"]);

        var line = SettingValidationFailure.Describe([thrown, multiLine]);

        AssertEx.True(line.StartsWith("Setting out of range: ", StringComparison.Ordinal), "The shell contract: the exact prefix first.");
        AssertEx.Contains(line, "RetentionAge");
        AssertEx.Contains(line, "; first second");
        AssertEx.False(line.Contains('\n', StringComparison.Ordinal), "One line, so the stderr tail carries all of it.");
    }

    // The production registration shape (Bind + ValidateDataAnnotations + ValidateOnStart) with a 30-second value.
    private static ServiceCollection OutOfRangeServices()
    {
        var services = new ServiceCollection();
        services.AddOptions<DevelopmentWorkspaceRetentionOptions>()
                .Configure(static options => options.RetentionAge = TimeSpan.FromSeconds(30))
                .ValidateDataAnnotations()
                .ValidateOnStart();
        return services;
    }
}
