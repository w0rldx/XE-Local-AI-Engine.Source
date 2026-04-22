using Microsoft.Extensions.Hosting;
using Projects;

var builder = DistributedApplication.CreateBuilder(args);

var ollama = builder.AddOllama("ollama")
                    .WithImageTag("latest")
                    .WithDataVolume();

var chatModel = ollama.AddModel("chat", "qwen3.5:0.8b");
var embeddingsModel = ollama.AddModel("embeddings", "qwen3-embedding:0.6b");

var nodeSqliteKey = builder.AddParameter("node-sqlite-key", true);
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
