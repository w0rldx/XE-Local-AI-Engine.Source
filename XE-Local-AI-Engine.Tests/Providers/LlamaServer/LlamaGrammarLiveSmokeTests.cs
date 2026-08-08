namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <para>
///         OPT-IN live smoke: starts a REAL <c>llama-server</c> and proves the shipped tool offer compiles into a GBNF
///         grammar on it. <see cref="LlamaGrammarToolSchemaCompatibilityTests" /> pins our side of the contract against
///         a bound we measured by hand; it cannot notice llama.cpp changing that limit, and no other suite can either —
///         <c>ChatLocalToolsE2ETests</c> looks like it covers the local-tools path but its chat backend is FakeOllama,
///         so no chat template, no GBNF and no sampler ever run there.
///     </para>
///     <para>
///         <b>The negative control is the load-bearing assertion.</b> A 200 on the sanitized offer proves nothing on its
///         own: a REASONING model emits <c>reasoning_content</c> first, so llama.cpp never enters the constrained branch
///         and never compiles a grammar at all — Qwen3.6-27B returns 200 on the very payload that 400s on
///         Qwen2.5-0.5B. So this test also posts the offer WITHOUT the compatibility pass and REQUIRES a 400 carrying
///         <c>failed to parse grammar</c>. If that comes back 200, the run is inert and the test fails loudly rather
///         than banking a meaningless green.
///     </para>
///     <para>
///         The server is launched with <c>--jinja</c> because that is what production does
///         (<c>LlamaServerProcessSupervisor</c>) and because without it llama-server ignores the offered tools
///         entirely — which would make this smoke silently inert in a third way.
///     </para>
/// </summary>
public sealed class LlamaGrammarLiveSmokeTests
{
    /// <summary>Absolute path to a <c>llama-server</c> executable. Its presence is half the opt-in gate.</summary>
    private const string ServerPathVariable = "XE_TOOL_GRAMMAR_SMOKE_SERVER";

    /// <summary>Absolute path to a chat GGUF. Its presence is the other half of the opt-in gate.</summary>
    private const string ModelPathVariable = "XE_TOOL_GRAMMAR_SMOKE_MODEL";

    /// <summary>Optional. When set, a JSON verdict is written here so a runner can prove the test really executed.</summary>
    private const string EvidencePathVariable = "XE_TOOL_GRAMMAR_SMOKE_EVIDENCE_PATH";

    private const string ReadyTimeoutVariable = "XE_TOOL_GRAMMAR_SMOKE_READY_SECONDS";

    /// <summary>
    ///     The exact llama.cpp surface of the P1: <c>{"error":{"code":400,"message":"Failed to initialize samplers:
    ///     failed to parse grammar",...}}</c>. Matched case-insensitively on the distinctive tail so a rewording of the
    ///     "Failed to initialize samplers" prefix does not turn a real detection into a false negative.
    /// </summary>
    private const string GrammarFailureSignature = "failed to parse grammar";

    private static readonly JsonSerializerOptions EvidenceJsonOptions = new()
    {
        WriteIndented = true
    };

    [Test]
    public async Task LiveLlamaServer_CompilesTheSanitizedToolOffer_AndStillRejectsTheUnsanitizedOne()
    {
        var serverPath = Environment.GetEnvironmentVariable(ServerPathVariable);
        var modelPath = Environment.GetEnvironmentVariable(ModelPathVariable);
        if (string.IsNullOrWhiteSpace(serverPath) || string.IsNullOrWhiteSpace(modelPath))
        {
            // Both gate variables are unique to this smoke, so a normal `dotnet test` can never trip it by accident —
            // unlike XE_LLAMACPP_SERVER_PATH, which developers on a GPU box legitimately export for the whole shell.
            Skip.Test($"Live tool-grammar smoke: set {ServerPathVariable} and {ModelPathVariable}, "
                      + "or run scripts/run-tool-grammar-smoke-local.sh.");
            return;
        }

        var server = RequireFile(ServerPathVariable, serverPath);
        var model = RequireFile(ModelPathVariable, modelPath);

        // Both bodies come from the REAL MEAI OpenAI adapter over the REAL production tool offer, so what is posted is
        // what the app posts — not a hand-serialized approximation of it.
        var offered = LlamaGrammarToolOffer.BuildProductionToolOffer();
        AssertEx.NotEmpty(offered, "the production tool offer must not be empty, or both posts below would be vacuous.");

        var original = new ChatOptions
        {
            Tools = [.. offered.Cast<AITool>()]
        };
        var unsanitizedBody = await LlamaGrammarToolOffer.CaptureWireBodyAsync(original, CancellationToken.None);
        var sanitizedBody = await LlamaGrammarToolOffer.CaptureWireBodyAsync(AssertEx.NotNull(DeferredLlamaServerChatClient.ApplyToolSchemaCompatibility(original)),
            CancellationToken.None);

        // Pre-flight: the negative control can only mean something while the raw offer still carries an uncompilable
        // bound. If the catalog ever drops below the limit on its own, say so here rather than letting the live 400
        // assertion fail with a confusing "the server accepted it" message.
        AssertEx.True(LlamaGrammarToolSchemaCompatibility.RequiresSanitizing(ExtractTools(unsanitizedBody)),
            "the unsanitized production offer no longer carries any bound above "
            + $"{LlamaGrammarToolSchemaCompatibility.MaxGrammarRepetitionBound}, so it is not a negative control any more. "
            + "Either the catalog changed or the compatibility pass leaked into the raw offer.");
        AssertEx.False(LlamaGrammarToolSchemaCompatibility.RequiresSanitizing(ExtractTools(sanitizedBody)),
            "the sanitized body must carry no bound above the compilable limit before it is worth posting.");

        var port = ReserveLoopbackPort();
        var process = StartServer(server, model, port, out var output);
        try
        {
            using var http = new HttpClient
            {
                BaseAddress = new Uri($"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/"),
                Timeout = TimeSpan.FromMinutes(10)
            };

            await WaitForHealthAsync(http, process, output).ConfigureAwait(false);

            var sanitized = await PostChatCompletionAsync(http, sanitizedBody).ConfigureAwait(false);
            AssertEx.Equal(HttpStatusCode.OK,
                sanitized.Status,
                "the SANITIZED production tool offer must compile into a grammar on a live llama-server. "
                + $"Server said: {Excerpt(sanitized.Body)}");

            var unsanitized = await PostChatCompletionAsync(http, unsanitizedBody).ConfigureAwait(false);
            AssertEx.False(unsanitized.Status == HttpStatusCode.OK, InertRunMessage(unsanitized.Body));
            AssertEx.Equal(HttpStatusCode.BadRequest,
                unsanitized.Status,
                $"the UNSANITIZED offer must be rejected with 400. Server said: {Excerpt(unsanitized.Body)}");
            AssertEx.Contains(unsanitized.Body,
                GrammarFailureSignature,
                StringComparison.OrdinalIgnoreCase,
                "the 400 must be the GRAMMAR failure this pass exists to prevent, not some unrelated rejection. "
                + $"Server said: {Excerpt(unsanitized.Body)}");

            await WriteEvidenceAsync(server, model, sanitized, unsanitized).ConfigureAwait(false);
        }
        finally
        {
            // A leaked llama-server holds the port and the VRAM (docs/agent-knowledge.md). Tear it down on every path,
            // including an assertion failure or a throw out of the health wait.
            StopServer(process);
        }
    }

    private static string RequireFile(string variable, string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
        {
            throw new InvalidOperationException($"{variable} is set to '{path}', which is not an existing file.");
        }

        return full;
    }

    /// <summary>
    ///     Message for the one outcome that must never be allowed to pass: the unsanitized offer was ACCEPTED. It names
    ///     both causes explicitly, because the two demand opposite responses — one means this run measured nothing, the
    ///     other means our constant is now wrong.
    /// </summary>
    private static string InertRunMessage(string body)
    {
        return "INERT RUN — the UNSANITIZED tool offer was ACCEPTED (HTTP 200), so this smoke proved nothing. "
               + "Exactly one of these is true, and they need opposite responses:\n"
               + "  (a) the model is a REASONING model: it emits reasoning_content first, so llama.cpp never enters the "
               + "constrained branch and never compiles the grammar. Re-run against a non-reasoning tool-capable model "
               + "(e.g. Qwen2.5-0.5B-Instruct) — this run measured nothing.\n"
               + "  (b) llama.cpp raised its repetition limits: re-measure the per-keyword and whole-tools-array ceiling "
               + $"and update LlamaGrammarToolSchemaCompatibility.MaxGrammarRepetitionBound (currently "
               + $"{LlamaGrammarToolSchemaCompatibility.MaxGrammarRepetitionBound}).\n"
               + $"Server said: {Excerpt(body)}";
    }

    /// <summary>
    ///     Picks a free loopback port by binding port 0 and releasing it. There is an unavoidable window between release
    ///     and llama-server's own bind; the server fails loudly on a taken port, so the failure mode is a clear startup
    ///     error rather than a silent cross-talk with someone else's server.
    /// </summary>
    private static int ReserveLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static Process StartServer(string serverPath, string modelPath, int port, out ConcurrentQueue<string> output)
    {
        var startInfo = new ProcessStartInfo(serverPath)
        {
            // The binary's own directory: a relocated source build resolves its .so siblings through an $ORIGIN RUNPATH,
            // and production launches it the same way (LlamaServerProcessSupervisor).
            WorkingDirectory = Path.GetDirectoryName(serverPath) ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        string[] arguments =
        [
            "-m", modelPath,
            "--host", "127.0.0.1",
            "--port", port.ToString(CultureInfo.InvariantCulture),
            "--parallel", "1",
            "--no-warmup",

            // Mandatory: without --jinja llama-server ignores the offered tools, so no grammar is ever compiled and the
            // whole smoke would pass vacuously. Production sets it for every chat process.
            "--jinja",
            "-c", "4096",
            "-ngl", "99"
        ];
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var log = new ConcurrentQueue<string>();
        output = log;

        var process = Process.Start(startInfo)
                      ?? throw new InvalidOperationException($"Could not start llama-server at '{serverPath}'.");

        process.OutputDataReceived += (_, args) => Enqueue(log, args.Data);
        process.ErrorDataReceived += (_, args) => Enqueue(log, args.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static void Enqueue(ConcurrentQueue<string> log, string? line)
    {
        if (line is null)
        {
            return;
        }

        log.Enqueue(line);
        while (log.Count > 200)
        {
            log.TryDequeue(out _);
        }
    }

    private static void StopServer(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(milliseconds: 30_000);
            }
        }
        catch (InvalidOperationException)
        {
            // The process object was already reaped; nothing left to kill.
        }
        finally
        {
            process.Dispose();
        }
    }

    private static async Task WaitForHealthAsync(HttpClient http, Process process, ConcurrentQueue<string> output)
    {
        var budget = TimeSpan.FromSeconds(ReadSeconds(ReadyTimeoutVariable, defaultSeconds: 300));
        var deadline = DateTimeOffset.UtcNow + budget;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException($"llama-server exited with code {process.ExitCode.ToString(CultureInfo.InvariantCulture)} before becoming healthy.\n"
                                                    + string.Join('\n', output));
            }

            try
            {
                using var response = await http.GetAsync(new Uri("health", UriKind.Relative)).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Not listening yet.
            }
            catch (TaskCanceledException)
            {
                // Probe timed out; the model is still loading.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
        }

        throw new InvalidOperationException($"llama-server did not report healthy within {budget.TotalSeconds.ToString(CultureInfo.InvariantCulture)}s "
                                            + $"(raise {ReadyTimeoutVariable} for a large model).\n"
                                            + string.Join('\n', output));
    }

    private static async Task<LiveResponse> PostChatCompletionAsync(HttpClient http, string wireBody)
    {
        // The captured body is posted byte-for-byte except for a max_tokens cap. Generation length has no bearing on
        // grammar compilation — the 400 happens at sampler init, before a single token — so capping it only keeps the
        // accepted case from writing a paragraph, and never weakens what is being judged.
        var payload = JsonNode.Parse(wireBody)?.AsObject()
                      ?? throw new InvalidOperationException("The captured wire body is not a JSON object.");
        payload["max_tokens"] = 8;

        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(new Uri("v1/chat/completions", UriKind.Relative), content).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return new LiveResponse(response.StatusCode, body);
    }

    private static JsonElement ExtractTools(string wireBody)
    {
        using var document = JsonDocument.Parse(wireBody);
        return document.RootElement.GetProperty("tools").Clone();
    }

    private static int ReadSeconds(string variable, int defaultSeconds)
    {
        var raw = Environment.GetEnvironmentVariable(variable);
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : defaultSeconds;
    }

    private static string Excerpt(string body)
    {
        var collapsed = body.ReplaceLineEndings(" ").Trim();
        return collapsed.Length <= 400 ? collapsed : collapsed[..400] + "…";
    }

    /// <summary>
    ///     Writes the verdict of BOTH posts. The runner script requires this file, so a skipped or half-run test can
    ///     never be mistaken for a pass by anything reading only the exit code — TUnit reports a skip as success.
    /// </summary>
    private static async Task WriteEvidenceAsync(string serverPath, string modelPath, LiveResponse sanitized, LiveResponse unsanitized)
    {
        var evidencePath = Environment.GetEnvironmentVariable(EvidencePathVariable);
        if (string.IsNullOrWhiteSpace(evidencePath))
        {
            return;
        }

        var payload = new
        {
            schemaVersion = 1,
            capturedAtUtc = DateTimeOffset.UtcNow,
            result = "passed",
            llamaServer = serverPath,
            model = modelPath,
            maxGrammarRepetitionBound = LlamaGrammarToolSchemaCompatibility.MaxGrammarRepetitionBound,
            sanitizedOffer = new
            {
                expected = 200,
                actual = (int)sanitized.Status
            },
            unsanitizedOffer = new
            {
                expected = 400,
                actual = (int)unsanitized.Status,
                signature = GrammarFailureSignature,
                message = Excerpt(unsanitized.Body)
            }
        };

        var fullPath = Path.GetFullPath(evidencePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)
                                  ?? throw new InvalidOperationException($"{EvidencePathVariable} has no parent directory."));
        await File.WriteAllTextAsync(fullPath, JsonSerializer.Serialize(payload, EvidenceJsonOptions) + Environment.NewLine)
                  .ConfigureAwait(false);
    }

    private sealed record LiveResponse(HttpStatusCode Status, string Body);
}
