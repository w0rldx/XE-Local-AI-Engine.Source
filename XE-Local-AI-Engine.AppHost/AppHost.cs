using Projects;

var builder = DistributedApplication.CreateBuilder(args);

var ollama = builder.AddOllama("ollama")
                    .WithImageTag("latest")
                    .WithGPUSupport()
                    .WithDataVolume();

var chatModel = ollama.AddModel("chat", "qwen3.5:9b");
var embeddingsModel = ollama.AddModel("embeddings", "nomic-embed-text");

builder.AddProject<XE_Local_AI_Engine_Client>("app", "https")
       .WithExternalHttpEndpoints()
       .WithUrlForEndpoint("https", url => url.DisplayText = "XE Local AI Engine (https)")
       .WithUrlForEndpoint("http", url => url.DisplayText = "XE Local AI Engine (http)")
       .WithEnvironment("ASPIRE_ENABLED", "true")
       .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
       .WithReference(chatModel)
       .WithReference(embeddingsModel)
       .WaitFor(chatModel)
       .WaitFor(embeddingsModel)
       .WithHttpHealthCheck("/health/live")
       .WithHttpHealthCheck("/health/ready");

await builder.Build().RunAsync();
