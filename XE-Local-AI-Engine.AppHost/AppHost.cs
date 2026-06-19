using Microsoft.Extensions.Hosting;
using Projects;

var builder = DistributedApplication.CreateBuilder(args);

// Pin a concrete Ollama version (>= 0.30.3) instead of "latest": the floating tag drifted to a cached
// 0.24.0 image that cannot load the gemma-4-12b ("gemma4") architecture, surfacing as an opaque 500.
// Bump this deliberately when adopting a newer Ollama. WithDataVolume keeps pulled models across recreation.
var ollama = builder.AddOllama("ollama")
                    .WithImageTag("0.30.5")
                    .WithDataVolume();

var chatModel = ollama.AddModel("chat", "qwen3:0.6b");
var embeddingsModel = ollama.AddModel("embeddings", "qwen3-embedding:0.6b");

var nodeSqliteKey = builder.AddParameter("node-sqlite-key", true);
var nodeSqlitePath = Path.Combine(builder.AppHostDirectory, ".data", "node-sqlite");

var nodeSqlite = builder.AddSqlite("node-sqlite", nodeSqlitePath, "node-chat.db");

if (builder.Environment.IsDevelopment())
{
    nodeSqlite = nodeSqlite.WithSqliteWeb();
}

// The in-Aspire HostAgent.Linux (Docker) sandbox/runtime project and the HostAgent gRPC client were removed:
// inference and the AgentHome sandbox now run as host processes (process sandbox provider), so no HostAgent
// resource or socket/HMAC/startup-gate wiring exists.

var app = builder.AddProject<XE_Local_AI_Engine_Client>("app", "https")
                 .WithExternalHttpEndpoints()
                 .WithUrlForEndpoint("https", url => url.DisplayText = "XE Local AI Engine (https)")
                 .WithUrlForEndpoint("http", url => url.DisplayText = "XE Local AI Engine (http)")
                 .WithEnvironment("ASPIRE_ENABLED", "true")
                 .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
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
       .WithBrowserLogs("/usr/bin/chromium-browser",
           userDataMode: BrowserUserDataMode.Isolated);

await builder.Build().RunAsync();
