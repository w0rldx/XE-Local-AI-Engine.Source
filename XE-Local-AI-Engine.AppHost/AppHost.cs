using Aspire.Hosting;
using Microsoft.Extensions.Hosting;
using Projects;

var builder = DistributedApplication.CreateBuilder(args);

var ollama = builder.AddOllama("ollama")
                    .WithImageTag("latest")
                    .WithGPUSupport()
                    .WithDataVolume();

var chatModel = ollama.AddModel("chat", "qwen3.5:9b");
var embeddingsModel = ollama.AddModel("embeddings", "nomic-embed-text");

var nodeSqliteKey = builder.AddParameter("node-sqlite-key", secret: true);
var nodeSqlitePath = Path.Combine(builder.AppHostDirectory, ".data", "node-sqlite");

var nodeSqlite = builder.AddSqlite("node-sqlite", nodeSqlitePath, "node-chat.db");

if (builder.Environment.IsDevelopment())
{
    nodeSqlite = nodeSqlite.WithSqliteWeb();
}

builder.AddProject<XE_Local_AI_Engine_Client>("app", "https")
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

await builder.Build().RunAsync();
