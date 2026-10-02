namespace XE_Local_AI_Engine.Tests.Scheduler;

using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using XE_Local_AI_Engine.Client;
using XE_Local_AI_Engine.Client.Services.ModelFit.Implementation;
using XE_Local_AI_Engine.Client.Services.Scheduler.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Client.Services.NodeSettings.Implementation;

/// <summary>
///     <c>Scheduler:Enabled=false</c> registers no Quartz runtime, yet the Scheduler endpoints stay discovered and
///     FastEndpoints activates them at startup. The node must still boot and the API must refuse cleanly.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class SchedulerFeatureGateTests
{
    private const string ApiPrefix = "/api/local/v1";

    [Test]
    public async Task SchedulerRoutes_WhenDisabled_TheNodeStartsAndAnswersFeatureDisabled()
    {
        await using var factory = new TestServerWebAppFactory
        {
            AdditionalConfiguration = new Dictionary<string, string?>
            {
                ["Scheduler:Enabled"] = "false"
            }
        };
        using var client = factory.CreateClient();

        foreach (var route in new[] { $"{ApiPrefix}/scheduler/jobs", $"{ApiPrefix}/scheduler/templates", $"{ApiPrefix}/scheduler/runs" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, route);
            factory.AddNodeBearerToken(request);
            using var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode, $"{route}: {body}");
            AssertEx.True(body.Contains(DisabledScheduledJobManagementService.DisabledMessage, StringComparison.Ordinal), body);
        }
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, true)]
    public void AddServices_RegistersTheRecommendationSeeder_OnlyWhenTheSchedulerIsOn(bool schedulerEnabled, bool expected)
    {
        // The seeder calls the management service in StartAsync, and the refusing one would stop the host. The host
        // factory removes every hosted service, so this registration check is the pin.
        var root = Path.Combine(Path.GetTempPath(), $"xe-scheduler-gate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = Environments.Development,
                ContentRootPath = root
            });
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Agent:LocalChat:DefaultModel"] = "llama3.2",
                ["CentralPlatform:BaseUrl"] = "https://127.0.0.1",
                ["ConnectionStrings:node-sqlite"] = $"Data Source={Path.Combine(root, "node.sqlite")}",
                ["Ollama:Endpoint"] = "http://127.0.0.1:11434",
                ["Scheduler:Enabled"] = schedulerEnabled.ToString()
            });

            builder.AddServices(builder.Configuration, NodeStartupSettings.Read(builder.Configuration, builder.Environment));

            var registered = builder.Services.Any(static descriptor => descriptor.ServiceType == typeof(IHostedService)
                                                                       && descriptor.ImplementationType == typeof(ModelRecommendationScheduleSeeder));
            AssertEx.Equal(expected, registered);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
