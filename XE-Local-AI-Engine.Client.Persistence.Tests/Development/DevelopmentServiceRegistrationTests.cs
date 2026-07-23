namespace XE_Local_AI_Engine.Client.Persistence.Tests.Development;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using XE_Local_AI_Engine.Client.DependencyInjection.Modules;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Development;

public sealed class DevelopmentServiceRegistrationTests
{
    [Test]
    public void AddNodeDevelopment_WhenDisabled_RegistersNoRuntimeServices()
    {
        var builder = Host.CreateApplicationBuilder();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Development:Enabled"] = "false"
        }).Build();
        builder.AddNodeDevelopment(configuration);
        using var provider = builder.Services.BuildServiceProvider();
        AssertEx.Null(provider.GetService<IDevelopmentCoordinator>());
        AssertEx.Null(provider.GetService<IDevelopmentArtifactBlobStore>());
    }

    [Test]
    public void AddNodeDevelopment_WhenConfigurationIsAbsent_RegistersCompleteRuntimeServices()
    {
        var builder = Host.CreateApplicationBuilder();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Development:MaxArtifactBytes"] = "1024"
        }).Build();
        builder.AddNodeDevelopment(configuration);
        AssertEx.True(builder.Services.Any(descriptor => descriptor.ServiceType == typeof(IDevelopmentStore)));
        AssertEx.True(builder.Services.Any(descriptor => descriptor.ServiceType == typeof(IDevelopmentCoordinator)));
        AssertEx.True(builder.Services.Any(descriptor => descriptor.ServiceType == typeof(IDevelopmentArtifactBlobStore)));
        AssertEx.True(builder.Services.Any(descriptor => descriptor.ServiceType == typeof(IDevelopmentCoderAttemptRunner)));
        AssertEx.True(builder.Services.Any(descriptor => descriptor.ServiceType == typeof(IDevelopmentValidationRunner)));
        AssertEx.True(builder.Services.Any(descriptor => descriptor.ServiceType == typeof(IDevelopmentReviewerAttemptRunner)));
        AssertEx.True(builder.Services.Any(descriptor => descriptor.ServiceType == typeof(IDevelopmentApplyService)));
        AssertEx.True(builder.Services.Any(descriptor => descriptor.ServiceType == typeof(IDevelopmentRepositoryBindingService)));
        AssertEx.True(builder.Services.Any(descriptor => descriptor.ServiceType == typeof(IDevelopmentCloudAttemptContextService)));
        AssertEx.True(builder.Services.Any(descriptor => descriptor.ServiceType == typeof(ICloudEgressAuthorizer)));
        AssertEx.True(builder.Services.Any(descriptor => descriptor.ServiceType == typeof(IHostedService)
                                                         && descriptor.ImplementationType == typeof(DevelopmentStartupReconciler)));
    }
}
