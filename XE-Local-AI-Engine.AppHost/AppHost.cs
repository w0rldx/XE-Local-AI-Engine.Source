using Projects;

var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<XE_Local_AI_Engine_Client>("app", launchProfileName: "https")
       .WithExternalHttpEndpoints()
       .WithUrlForEndpoint("https", url => url.DisplayText = "XE Local AI Engine (https)")
       .WithUrlForEndpoint("http", url => url.DisplayText = "XE Local AI Engine (http)")
       .WithEnvironment("ASPIRE_ENABLED", "true")
       .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
       .WithHttpHealthCheck("/health/live")
       .WithHttpHealthCheck("/health/ready");

await builder.Build().RunAsync();
