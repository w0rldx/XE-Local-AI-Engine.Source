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
///         Qwen2.5-0.5B. So this test also posts a synthetic tool WITHOUT the compatibility pass and REQUIRES a 400
///         carrying <c>failed to parse grammar</c>. If that comes back 200, the run is inert and the test fails loudly
///         rather than banking a meaningless green.
///     </para>
///     <para>
///         The control is synthetic, not the raw production offer, because the production offer's large bounds are all
///         upper bounds, and llama.cpp from b11669 (upstream #26613) silently drops an upper bound above 2000 instead of
///         failing. A <c>minLength</c> above 2000 still fails on every build from the b10201 floor through b11803.
///     </para>
///     <para>
///         The server is launched with <c>--jinja</c> because that is what production does
///         (<c>LlamaServerProcessSupervisor</c>) and because without it llama-server ignores the offered tools
///         entirely — which would make this smoke silently inert in a third way.
///     </para>
/// </summary>
[Category(TestCategories.ExternalInfra)]
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
    ///     The exact llama.cpp surface of the failure: <c>{"error":{"code":400,"message":"Failed to initialize samplers:
    ///     failed to parse grammar",...}}</c>. Matched case-insensitively on the distinctive tail so a rewording of the
    ///     "Failed to initialize samplers" prefix does not turn a real detection into a false negative.
    /// </summary>
    private const string GrammarFailureSignature = "failed to parse grammar";

    /// <summary>
    ///     The negative control's one bound: a <c>minLength</c> above llama.cpp's repetition threshold (2000). Every build
    ///     from the b10201 floor through b11803 rejects it, and the compatibility pass strips it.
    /// </summary>
    private const int NegativeControlMinLength = 4096;

    private const string NegativeControlToolName = "grammar_negative_control";

    private static readonly JsonSerializerOptions EvidenceJsonOptions = new()
    {
        WriteIndented = true
    };

    [Test]
    public async Task LiveLlamaServer_CompilesTheSanitizedToolOffer_AndRejectsTheUnsanitizedNegativeControl()
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

        // Both bodies come from the REAL MEAI OpenAI adapter, so what is posted is what the app posts — not a
        // hand-serialized approximation of it. The sanitized half carries the REAL production offer plus the control
        // tool, which proves the shipped catalog compiles AND that the pass neutralizes the control's bound.
        var offered = LlamaGrammarToolOffer.BuildProductionToolOffer();
        AssertEx.NotEmpty(offered, "the production tool offer must not be empty, or the sanitized post below would be vacuous.");

        var control = LlamaGrammarToolOffer.BuildTool(NegativeControlToolName,
            $$$"""{"type":"object","properties":{"value":{"type":"string","minLength":{{{NegativeControlMinLength}}}}},"required":["value"]}""");
        var positive = new ChatOptions
        {
            Tools = [.. offered.Cast<AITool>(), control]
        };
        var negative = new ChatOptions
        {
            Tools = [control]
        };
        var sanitizedBody = await LlamaGrammarToolOffer.CaptureWireBodyAsync(AssertEx.NotNull(DeferredLlamaServerChatClient.ApplyToolSchemaCompatibility(positive)),
            CancellationToken.None);
        var negativeControlBody = await LlamaGrammarToolOffer.CaptureWireBodyAsync(negative, CancellationToken.None);

        // Pre-flight: the control means something only while the pass would sanitize it and the posted body still
        // carries it raw. Say so here rather than letting the live 400 assertion fail with "the server accepted it".
        AssertEx.True(LlamaGrammarToolSchemaCompatibility.RequiresSanitizing(ExtractTools(negativeControlBody)),
            $"the negative control's minLength {NegativeControlMinLength} is no longer above "
            + $"{LlamaGrammarToolSchemaCompatibility.MaxGrammarRepetitionBound}, or the pass leaked into the raw body, "
            + "so it is not a negative control any more.");
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

            await WaitForHealthAsync(http, process, output);

            var sanitized = await PostChatCompletionAsync(http, sanitizedBody);
            AssertEx.Equal(HttpStatusCode.OK,
                sanitized.Status,
                "the SANITIZED production tool offer must compile into a grammar on a live llama-server. "
                + $"Server said: {Excerpt(sanitized.Body)}");

            var negativeControl = await PostChatCompletionAsync(http, negativeControlBody);
            AssertEx.False(negativeControl.Status == HttpStatusCode.OK, InertRunMessage(negativeControl.Body));
            AssertEx.Equal(HttpStatusCode.BadRequest,
                negativeControl.Status,
                $"the UNSANITIZED negative control must be rejected with 400. Server said: {Excerpt(negativeControl.Body)}");
            AssertEx.Contains(negativeControl.Body,
                GrammarFailureSignature,
                StringComparison.OrdinalIgnoreCase,
                "the 400 must be the GRAMMAR failure this pass exists to prevent, not some unrelated rejection. "
                + $"Server said: {Excerpt(negativeControl.Body)}");

            await WriteEvidenceAsync(server, model, sanitized, negativeControl);
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
    ///     Message for the one outcome that must never pass: the unsanitized negative control was ACCEPTED. It names both
    ///     causes, because they demand opposite responses: one means this run measured nothing, the other a changed build.
    /// </summary>
    private static string InertRunMessage(string body)
    {
        return $"INERT RUN — the UNSANITIZED negative control (one tool, minLength {NegativeControlMinLength}) was ACCEPTED "
               + "(HTTP 200), so this smoke proved nothing. Exactly one of these is true, and they need opposite responses:\n"
               + "  (a) the model is a REASONING model: it emits reasoning_content first, so llama.cpp never enters the "
               + "constrained branch and never compiles the grammar. Re-run against a non-reasoning tool-capable model "
               + "(e.g. Qwen2.5-0.5B-Instruct) — this run measured nothing.\n"
               + "  (b) this llama.cpp build no longer rejects a minimum repetition above its threshold: re-measure the "
               + "per-keyword ceiling on it, pick a control bound it still rejects, and re-check "
               + $"LlamaGrammarToolSchemaCompatibility.MaxGrammarRepetitionBound (currently "
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
                using var response = await http.GetAsync(new Uri("health", UriKind.Relative));
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

            // real-timer: waits on a real spawned llama-server loading a model; /health is the only readiness signal.
            await Task.Delay(TimeSpan.FromMilliseconds(500));
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
        using var response = await http.PostAsync(new Uri("v1/chat/completions", UriKind.Relative), content);
        var body = await response.Content.ReadAsStringAsync();
        return new LiveResponse
        {
            Status = response.StatusCode,
            Body = body
        };
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
    private static async Task WriteEvidenceAsync(string serverPath, string modelPath, LiveResponse sanitized, LiveResponse negativeControl)
    {
        var evidencePath = Environment.GetEnvironmentVariable(EvidencePathVariable);
        if (string.IsNullOrWhiteSpace(evidencePath))
        {
            return;
        }

        var payload = new
        {
            schemaVersion = 2,
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
            negativeControl = new
            {
                tool = NegativeControlToolName,
                minLength = NegativeControlMinLength,
                expected = 400,
                actual = (int)negativeControl.Status,
                signature = GrammarFailureSignature,
                message = Excerpt(negativeControl.Body)
            }
        };

        var fullPath = Path.GetFullPath(evidencePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)
                                  ?? throw new InvalidOperationException($"{EvidencePathVariable} has no parent directory."));
        await File.WriteAllTextAsync(fullPath, JsonSerializer.Serialize(payload, EvidenceJsonOptions) + Environment.NewLine);
    }

    private sealed record LiveResponse
    {
        public required HttpStatusCode Status { get; init; }

        public required string Body { get; init; }
    }
}
