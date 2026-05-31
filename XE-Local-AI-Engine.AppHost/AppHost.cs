using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Projects;

var builder = DistributedApplication.CreateBuilder(args);
var enableHostAgentDev = builder.Configuration.GetValue<bool>("XE_ENABLE_HOST_AGENT_DEV");
var enableHostAgentRuntimeFidelity = builder.Configuration.GetValue<bool>("XE_ENABLE_HOST_AGENT_RUNTIME_FIDELITY");
var hostAgentMode = "disabled";
if (enableHostAgentDev)
{
    hostAgentMode = "fast-dev";
}

if (enableHostAgentRuntimeFidelity)
{
    hostAgentMode = "runtime-fidelity";
}

var ollama = builder.AddOllama("ollama")
                    .WithImageTag("latest")
                    .WithDataVolume();

var chatModel = ollama.AddModel("chat", "qwen3.5:0.8b");
var embeddingsModel = ollama.AddModel("embeddings", "qwen3-embedding:0.6b");

var nodeSqliteKey = builder.AddParameter("node-sqlite-key", true);
var nodeSqlitePath = Path.Combine(builder.AppHostDirectory, ".data", "node-sqlite");

var nodeSqlite = builder.AddSqlite("node-sqlite", nodeSqlitePath, "node-chat.db");
var hostAgentDataDirectoryName = enableHostAgentRuntimeFidelity
    ? "host-agent-runtime-fidelity"
    : "host-agent-dev";
// Unix domain socket paths are capped at 108 chars (sun_path). The AppHost project dir
// is too deep (>108), so on Unix root the dev socket under a short temporary directory to stay under the limit.
var hostAgentSocketPath = OperatingSystem.IsWindows()
    ? Path.Combine(builder.AppHostDirectory, ".data", hostAgentDataDirectoryName, "host-agent.sock")
    : Path.Combine(Path.GetTempPath(), "xe-ha", hostAgentDataDirectoryName, "host-agent.sock");
var hostAgentDockerEndpoint = builder.Configuration["XE_HOST_AGENT_DOCKER_ENDPOINT"]
                              ?? builder.Configuration["HostAgent:Docker:Endpoint"];

if (builder.Environment.IsDevelopment())
{
    nodeSqlite = nodeSqlite.WithSqliteWeb();
}

IResourceBuilder<ProjectResource>? hostAgentLinux = null;
IResourceBuilder<ParameterResource>? hostAgentHmacSecret = null;
if (enableHostAgentDev || enableHostAgentRuntimeFidelity)
{
    hostAgentHmacSecret = builder.AddParameter("host-agent-hmac-secret",
        new GenerateParameterDefault
        {
            MinLength = 64,
            Special = false
        },
        true,
        true);
    hostAgentLinux = builder.AddProject<XE_Local_AI_Engine_HostAgent_Linux>("xe-host-agent-linux")
                            .WithEnvironment("ASPIRE_ENABLED", "true")
                            .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
                            .WithEnvironment("XE_HOST_AGENT_ASPIRE_MODE", hostAgentMode)
                            .WithEnvironment("XE_HOST_AGENT_SOCKET", hostAgentSocketPath)
                            .WithEnvironment("XE_HOST_AGENT_TCP_DISABLED", "true")
                            .WithEnvironment("HostAgent__Hmac__Secret", hostAgentHmacSecret)
                            .WithEnvironment("HostAgent__Docker__UseFakeDriver", enableHostAgentRuntimeFidelity ? "false" : "true")
                            .WithReference(chatModel)
                            .WaitFor(chatModel);

    if (enableHostAgentRuntimeFidelity && !string.IsNullOrWhiteSpace(hostAgentDockerEndpoint))
    {
        hostAgentLinux.WithEnvironment("HostAgent__Docker__Endpoint", hostAgentDockerEndpoint);
    }
}

var app = builder.AddProject<XE_Local_AI_Engine_Client>("app", "https")
                 .WithExternalHttpEndpoints()
                 .WithUrlForEndpoint("https", url => url.DisplayText = "XE Local AI Engine (https)")
                 .WithUrlForEndpoint("http", url => url.DisplayText = "XE Local AI Engine (http)")
                 .WithEnvironment("ASPIRE_ENABLED", "true")
                 .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
                 .WithEnvironment("XE_HOST_AGENT_ASPIRE_MODE", hostAgentMode)
                 .WithEnvironment("XE_NODE_SQLITE_KEY", nodeSqliteKey)
                 .WithEnvironment("NodeAuth__Jwt__Issuer", "xe-local-ai-engine")
                 .WithEnvironment("NodeAuth__Jwt__Audience", "xe-local-ai-engine")
                 .WithReference(chatModel)
                 .WithReference(embeddingsModel)
                 .WithReference(nodeSqlite)
                 .WaitFor(chatModel)
                 .WaitFor(embeddingsModel)
                 .WaitFor(nodeSqlite)
                 .WithHttpHealthCheck("/health/live")
                 .WithHttpHealthCheck("/health/ready")
                 .WithUrls(static context =>
                 {
                     if (context.GetEndpoint("https") is { } https)
                     {
                         context.Urls.Add(new ResourceUrlAnnotation
                         {
                             Url = "/scalar",
                             DisplayText = "Scalar API docs",
                             Endpoint = https
                         });
                         context.Urls.Add(new ResourceUrlAnnotation
                         {
                             Url = "/openapi/local/v1/v1.json",
                             DisplayText = "OpenAPI spec (v1)",
                             Endpoint = https
                         });
                         context.Urls.Add(new ResourceUrlAnnotation
                         {
                             Url = "/devui",
                             DisplayText = "Microsoft Agent DevUI",
                             Endpoint = https
                         });
                     }
                 });

builder.AddViteApp("client-react", "../XE-Local-AI-Engine.Client.React")
       .WithEnvironment("BROWSER", "none")
       .WithEnvironment("VITE_APP_TITLE", "XE Local AI Engine")
       .WithEnvironment("VITE_API_VERSION", "v1")
       .WithEnvironment("VITE_CROSS_COOKIE_ENABLED", "false")
       .WithHttpsEndpoint(env: "VITE_PORT", port: 5175)
       .WithReference(app)
       .WaitFor(app)
       .WithEnvironment("VITE_PROXY_TARGET", $"{app.GetEndpoint("https")}")
       .WithRunScript("dev")
       .WithBuildScript("build")
       .WithPnpm()
       .WithBrowserLogs(browser: "/usr/bin/chromium-browser",
           userDataMode: BrowserUserDataMode.Isolated);

if (hostAgentLinux is not null && hostAgentHmacSecret is not null)
{
    app.WithEnvironment("XE_HOST_AGENT_SOCKET", hostAgentSocketPath)
       .WithEnvironment("HostAgent__Client__SocketPath", hostAgentSocketPath)
       .WithEnvironment("HostAgent__Client__Secret", hostAgentHmacSecret)
       .WithEnvironment("HostAgent__StartupGate__Enabled", "true")
       .WithEnvironment("HostAgent__StartupGate__SocketPath", hostAgentSocketPath)
       .WithEnvironment("HostAgent__StartupGate__Secret", hostAgentHmacSecret)
       .WaitFor(hostAgentLinux);
}

await builder.Build().RunAsync();
