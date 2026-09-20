using Microsoft.Extensions.Hosting;
using Projects;

var builder = DistributedApplication.CreateBuilder(args);

var nodeSqliteKey = builder.AddParameter("node-sqlite-key", true);

var nodeSqlitePath = Path.Combine(builder.AppHostDirectory, ".data", "node-sqlite");

var nodeSqlite = builder.AddSqlite("node-sqlite", nodeSqlitePath, "node-chat.db");

if (builder.Environment.IsDevelopment())
{
    nodeSqlite = nodeSqlite.WithSqliteWeb();
}

var app = builder.AddProject<XE_Local_AI_Engine_Client>("app", "https")
                 .WithExternalHttpEndpoints()
                 .WithUrlForEndpoint("https", url => url.DisplayText = "XE Local AI Engine (https)")
                 .WithUrlForEndpoint("http", url => url.DisplayText = "XE Local AI Engine (http)")
                 .WithEnvironment("ASPIRE_ENABLED", "true")
                 .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
                 .WithEnvironment("XE_NODE_SQLITE_KEY", nodeSqliteKey)
                 .WithEnvironment("NodeAuth__Jwt__Issuer", "xe-local-ai-engine")
                 .WithEnvironment("NodeAuth__Jwt__Audience", "xe-local-ai-engine")
                 .WithReference(nodeSqlite)
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
                     }
                 });

// Aspire's browser-log integration is still experimental. Keep the diagnostic local to the one development-only
// resource that deliberately opts into forwarding Chrome logs instead of suppressing it for the whole AppHost.
#pragma warning disable ASPIREBROWSERLOGS001
builder.AddViteApp("client-react", "../XE-Local-AI-Engine.Client.React")
       .WithEnvironment("BROWSER", "none")
       .WithEnvironment("VITE_APP_TITLE", "XE Local AI Engine")
       .WithEnvironment("VITE_API_VERSION", "v1")
       .WithEnvironment("VITE_CROSS_COOKIE_ENABLED", "false")
       // Reconfigure the ONE endpoint AddViteApp already provisions rather than adding a second, which the DCP proxy would claim
       // and Vite never serve. See docs/wiki/11-hosting-and-deployment.md ("Aspire AppHost (dev/integration)").
       .WithEndpoint("http", endpoint =>
       {
           endpoint.UriScheme = "https";
           endpoint.Port = 5175;
       })
       .WithReference(app)
       .WaitFor(app)
       .WithEnvironment("VITE_PROXY_TARGET", $"{app.GetEndpoint("https")}")
       .WithRunScript("dev")
       .WithBuildScript("build")
       .WithPnpm()
       .WithBrowserLogs("/usr/bin/google-chrome",
           userDataMode: BrowserUserDataMode.Isolated);
#pragma warning restore ASPIREBROWSERLOGS001

await builder.Build().RunAsync();
