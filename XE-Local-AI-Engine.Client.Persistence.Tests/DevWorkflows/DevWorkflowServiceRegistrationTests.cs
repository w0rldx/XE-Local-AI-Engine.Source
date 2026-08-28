namespace XE_Local_AI_Engine.Client.Persistence.Tests.DevWorkflows;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using XE_Local_AI_Engine.Client.DependencyInjection.Modules;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;

public sealed class DevWorkflowServiceRegistrationTests
{
    [Test]
    public void AddNodeDevWorkflows_RegistersTheStoreAndTheBlobStore()
    {
        var builder = Host.CreateApplicationBuilder();
        _ = builder.AddNodeDevWorkflows(new ConfigurationBuilder().Build());

        AssertEx.True(builder.Services.Any(descriptor => descriptor.ServiceType == typeof(IDevWorkflowStore)));
        AssertEx.True(builder.Services.Any(descriptor => descriptor.ServiceType == typeof(IDevWorkflowArtifactBlobStore)));
    }

    [Test]
    public void AddNodeDevWorkflows_WhenDisabled_StillRegistersEverything()
    {
        var builder = Host.CreateApplicationBuilder();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DevWorkflows:Enabled"] = "false"
        }).Build();
        _ = builder.AddNodeDevWorkflows(configuration);

        // The kill switch gates behaviour, not the container: a disabled node has to answer legibly rather than 500 out
        // of an empty container.
        AssertEx.True(builder.Services.Any(descriptor => descriptor.ServiceType == typeof(IDevWorkflowStore)));
        AssertEx.True(builder.Services.Any(descriptor => descriptor.ServiceType == typeof(IDevWorkflowArtifactBlobStore)));
    }

    [Test]
    public void CompositionRoot_InvokesTheModuleAfterWorkSessionsAndDevelopment()
    {
        // A workflow is the composition of the two: its agent nodes own work sessions and its DevTask nodes drive
        // Development Mode tasks. The invariant is a property of the composition root's call order, which is why it is
        // read from the source rather than from a container.
        var source = File.ReadAllText(CompositionRootPath());
        var workSessionIndex = source.IndexOf("AddNodeWorkSessions(configuration)", StringComparison.Ordinal);
        var developmentIndex = source.IndexOf("AddNodeDevelopment(configuration)", StringComparison.Ordinal);
        var devWorkflowIndex = source.IndexOf("AddNodeDevWorkflows(configuration)", StringComparison.Ordinal);

        AssertEx.True(workSessionIndex >= 0, "The composition root must still call AddNodeWorkSessions.");
        AssertEx.True(developmentIndex >= 0, "The composition root must still call AddNodeDevelopment.");
        AssertEx.True(devWorkflowIndex > workSessionIndex, "AddNodeDevWorkflows must be invoked after AddNodeWorkSessions.");
        AssertEx.True(devWorkflowIndex > developmentIndex, "AddNodeDevWorkflows must be invoked after AddNodeDevelopment.");
    }

    private static string CompositionRootPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "XE-Local-AI-Engine.slnx")))
        {
            directory = directory.Parent;
        }

        var root = AssertEx.NotNull(directory, "The repository root must be reachable from the test output directory.");
        return Path.Combine(root.FullName, "XE-Local-AI-Engine.Client.Application", "DependencyInjection", "NodeApplicationServiceCollectionExtensions.cs");
    }
}
