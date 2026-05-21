using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Projects;

var builder = DistributedApplication.CreateBuilder(args);
var enableHostAgentDev = builder.Configuration.GetValue<bool>("XE_ENABLE_HOST_AGENT_DEV");

var ollama = builder.AddOllama("ollama")
                    .WithImageTag("latest")
                    .WithDataVolume();

var chatModel = ollama.AddModel("chat", "qwen3.5:0.8b");
var embeddingsModel = ollama.AddModel("embeddings", "qwen3-embedding:0.6b");

var nodeSqliteKey = builder.AddParameter("node-sqlite-key", true);
var nodeSqlitePath = Path.Combine(builder.AppHostDirectory, ".data", "node-sqlite");

var nodeSqlite = builder.AddSqlite("node-sqlite", nodeSqlitePath, "node-chat.db");
var hostAgentSocketPath = Path.Combine(builder.AppHostDirectory, ".data", "host-agent-dev", "host-agent.sock");

if (builder.Environment.IsDevelopment())
{
    nodeSqlite = nodeSqlite.WithSqliteWeb();
}

IResourceBuilder<ProjectResource>? hostAgentLinux = null;
IResourceBuilder<ParameterResource>? hostAgentHmacSecret = null;
if (enableHostAgentDev)
{
    hostAgentHmacSecret = builder.AddParameter("host-agent-hmac-secret", true);
    hostAgentLinux = builder.AddProject<XE_Local_AI_Engine_HostAgent_Linux>("xe-host-agent-linux")
                            .WithEnvironment("ASPIRE_ENABLED", "true")
                            .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
                            .WithEnvironment("XE_HOST_AGENT_SOCKET", hostAgentSocketPath)
                            .WithEnvironment("XE_HOST_AGENT_TCP_DISABLED", "true")
                            .WithEnvironment("HostAgent__Hmac__Secret", hostAgentHmacSecret)
                            .WithEnvironment("HostAgent__Docker__UseFakeDriver", "true")
                            .WithReference(chatModel)
                            .WaitFor(chatModel);
}

var app = builder.AddProject<XE_Local_AI_Engine_Client>("app", "https")
                 .WithExternalHttpEndpoints()
                 .WithUrlForEndpoint("https", url => url.DisplayText = "XE Local AI Engine (https)")
                 .WithUrlForEndpoint("http", url => url.DisplayText = "XE Local AI Engine (http)")
                 .WithEnvironment("ASPIRE_ENABLED", "true")
                 .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
                 .WithEnvironment("XE_NODE_SQLITE_KEY", nodeSqliteKey)
                 .WithReference(chatModel)
                 .WithReference(embeddingsModel)
                 .WithReference(nodeSqlite)
                 .WaitFor(chatModel)
                 .WaitFor(embeddingsModel)
                 .WaitFor(nodeSqlite)
                 .WithHttpHealthCheck("/health/live")
                 .WithHttpHealthCheck("/health/ready");

if (hostAgentLinux is not null && hostAgentHmacSecret is not null)
{
    app.WithEnvironment("HostAgent__Client__SocketPath", hostAgentSocketPath)
       .WithEnvironment("HostAgent__Client__Secret", hostAgentHmacSecret)
       .WaitFor(hostAgentLinux);
}

await builder.Build().RunAsync();
