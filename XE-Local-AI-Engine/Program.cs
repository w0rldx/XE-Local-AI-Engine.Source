using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OllamaSharp;
using XE_Local_AI_Engine.Components;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
       .AddInteractiveServerComponents();

// Load configuration
var ollamaEndpoint = builder.Configuration.GetValue<string>("Ollama:Endpoint") ?? "http://127.0.0.1:11434";
var chatModel = builder.Configuration.GetValue<string>("Ollama:ChatModel") ?? "qwen3.5:9b";
var ollamaUri = new Uri(ollamaEndpoint);

#pragma warning disable CA2000 // Dispose objects before losing scope - lifetime managed by DI container
IChatClient ollamaApiClient = new OllamaApiClient(ollamaUri, chatModel);
#pragma warning restore CA2000

builder.Services.AddSingleton<IChatClient>(_ => ollamaApiClient);

// Register AI Agent with dependency injection
builder.Services.AddSingleton<AIAgent>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("Configuring AI Agent with Claude model '{Model}'", chatModel);

    var chatClient = sp.GetRequiredService<IChatClient>();
    return chatClient.CreateAIAgent(name: "ClaudeChat",
        instructions: "You are a helpful and friendly AI assistant.");
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseAntiforgery();

app.UseStaticFiles();
app.MapRazorComponents<App>()
   .AddInteractiveServerRenderMode();

await app.RunAsync();
