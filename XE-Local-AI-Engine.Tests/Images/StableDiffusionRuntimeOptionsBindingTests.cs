namespace XE_Local_AI_Engine.Tests.Images;

using Microsoft.Extensions.Configuration;
using XE_Local_AI_Engine.Client.DependencyInjection.Modules;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Options;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The host binds the <c>StableDiffusionRuntime</c> section; the provider modules only TryAdd a bare default.
///     Until this binding existed no key of the section (env or appsettings) ever reached the sd-server supervisor.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class StableDiffusionRuntimeOptionsBindingTests
{
    [Test]
    public void Bind_ReadsTheSectionOverTheDefaults()
    {
        var configuration = new ConfigurationBuilder()
                            .AddInMemoryCollection([
                                new KeyValuePair<string, string?>("StableDiffusionRuntime:TextEncoderOnGpu", "true"),
                                new KeyValuePair<string, string?>("StableDiffusionRuntime:MaxLoadedProcesses", "2"),
                            ])
                            .Build();

        var options = AddNodeImagesExtensions.BindStableDiffusionRuntimeOptions(configuration);

        AssertEx.True(options.TextEncoderOnGpu, "the knob must come from configuration");
        AssertEx.Equal(expected: 2, options.MaxLoadedProcesses);
        AssertEx.Equal(new StableDiffusionRuntimeOptions().PortRangeStart, options.PortRangeStart, "an unset key keeps the class default");
    }

    [Test]
    public void Bind_WithoutTheSection_KeepsTheDefaults()
    {
        var options = AddNodeImagesExtensions.BindStableDiffusionRuntimeOptions(new ConfigurationBuilder().Build());

        AssertEx.False(options.TextEncoderOnGpu);
        AssertEx.Equal(new StableDiffusionRuntimeOptions().MaxLoadedProcesses, options.MaxLoadedProcesses);
    }
}
