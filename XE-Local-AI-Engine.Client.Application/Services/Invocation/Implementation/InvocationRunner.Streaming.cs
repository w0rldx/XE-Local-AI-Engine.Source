namespace XE_Local_AI_Engine.Client.Services.Invocation.Implementation;

using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Client.Common.Telemetry;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Encrypted;
using XE_Local_AI_Engine.Client.Services.Connection;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

public sealed partial class InvocationRunner
{
    // The mutable streaming accumulator shared by the single-agent and orchestration paths: the response/reasoning
    // builders, the byte totals (against _maxResponseSizeBytes), the monotonic sequence counters the transport sends,
    // and the terminal usage snapshot. Carried by reference into the branch methods so the post-stream completion
    // block in RunAsync reads the final state. Internal rather than private only so LocalRuntimeWarmer can record the
    // readiness telemetry (provider tag, ready timestamp, readiness duration) into the SAME accumulator the completion
    // block reads — including on the cancelled path, where the value must land before the cancellation propagates.
    internal sealed class StreamState
    {
        // Wall-clock generation timer for the whole turn (prompt-eval through final token), started at state
        // construction so it covers both the single-agent and orchestration branches. Read once in the completion
        // block to stamp the persisted tokens-per-second duration.
        public Stopwatch GenerationStopwatch { get; } = Stopwatch.StartNew();

        // Time-to-first-token inputs. HarnessStartedTimestamp provides the production-harness TTFT used by the
        // terminal efficiency record. ModelReadyTimestamp preserves the existing inference-only measure (the local
        // warm phase's completion, or turn start for a runtime with no cold load). ProviderTag is the bounded metric
        // dimension (local | remote). FirstOutputRecorded gates both one-shot values on the first emitted chunk.
        public long HarnessStartedTimestamp { get; init; }

        public long? ModelReadyTimestamp { get; set; }

        public double? ModelReadinessDurationMs { get; private set; }

        /// <summary>
        ///     Adds one local warm's duration to the turn's readiness total. SUMMED, not assigned: a turn can warm
        ///     twice — a dispatched fast model that fails before first output is followed by a warm of the original
        ///     model for the fallback — and an assignment charged the turn only the second warm while the whole-turn
        ///     clock still contained both.
        /// </summary>
        public void AddModelReadiness(double durationMs)
        {
            ModelReadinessDurationMs = (ModelReadinessDurationMs ?? 0d) + durationMs;
        }

        public double? FirstOutputLatencyMs { get; set; }

        public string ProviderTag { get; set; } = "remote";

        public bool FirstOutputRecorded { get; set; }

        public StringBuilder ResponseBuilder { get; } = new();

        public StringBuilder ReasoningBuilder { get; } = new();

        // The turn's usage, ACCUMULATED across every provider round, for the same reason AddSegmentTimings accumulates
        // llama-server's timings: FunctionInvokingChatClient runs the tool loop inside ONE RunStreamingAsync, so a
        // tool-calling turn emits one UsageContent per round and last-wins recorded only the final round (measured
        // live: a three-round turn reported prompt 2,970 against a per-round estimate of 10,722). This is what the turn
        // COST, and it feeds the token-usage metric, the efficiency record and the run-envelope row. Private setter so
        // the only way in is AddUsage below — an overwrite is exactly the bug this replaced.
        public UsageSnapshot? UsageSnapshot { get; private set; }

        // The LAST provider round's usage on its own. A round's prompt is the WHOLE conversation so far, not a delta, so
        // the final round's input count is what the model's context actually HELD when it answered — the occupancy the
        // chat meter derives from the assistant message's tokens. Summing rounds there reads as three times the context
        // the turn ever used (10,722 shown for a real ~3,000). Cost sums; occupancy does not.
        public UsageSnapshot? LastRoundUsage { get; private set; }

        /// <summary>Records one provider round's reported usage as the last round and folds it into the turn totals.</summary>
        public void AddUsage(UsageDetails usage)
        {
            LastRoundUsage = UsageSnapshot.From(usage);
            UsageSnapshot = UsageSnapshot.Accumulate(UsageSnapshot, LastRoundUsage);
        }

        // Why generation stopped, taken from the LAST streamed update that carried a finish reason: a tool-calling turn
        // ends its first segment with "tool_calls" and its final one with "stop", so last-wins is the turn's answer.
        // Verbatim ChatFinishReason.Value — the OpenAI-compatible llama-server emits "length" both when n_predict is
        // exhausted and when the context window fills (stopped_limit), which is exactly the truncation the benchmark
        // ranking must see. Null when no provider reported one.
        public string? FinishReason { get; set; }

        public long Sequence { get; set; }

        public long ReasoningSequence { get; set; }

        public int TotalResponseBytes { get; set; }

        public int TotalReasoningBytes { get; set; }

        // llama-server's own pp/tg timings, accumulated across every provider REQUEST the turn made. A tool-calling
        // turn is several requests, each carrying its own `timings` object, so the token counts and durations SUM: the
        // turn spent that much total time prefilling and that much decoding. TTFT is deliberately NOT summed —
        // FirstOutputLatencyMs above is already one-shot on the first emitted chunk, which belongs to the first request,
        // which is when the caller first saw output. Every field stays null for a provider that reports no timings.
        public int? PromptTokens { get; private set; }

        public double? PromptMs { get; private set; }

        public int? GenerationTokens { get; private set; }

        public double? GenerationMs { get; private set; }

        public int? CachedPromptTokens { get; private set; }

        /// <summary>How many provider requests reported timings — 1 for a plain turn, more once tools are called.</summary>
        public int SegmentCount { get; private set; }

        /// <summary>Folds one request's timings into the turn totals. A null reading (none reported) is a no-op.</summary>
        public void AddSegmentTimings(LlamaServerGenerationTimings? timings)
        {
            if (timings is null)
            {
                return;
            }

            SegmentCount++;
            PromptTokens = Add(PromptTokens, timings.PromptTokens);
            PromptMs = Add(PromptMs, timings.PromptMs);
            GenerationTokens = Add(GenerationTokens, timings.GenerationTokens);
            GenerationMs = Add(GenerationMs, timings.GenerationMs);
            CachedPromptTokens = Add(CachedPromptTokens, timings.CachedPromptTokens);
        }

        /// <summary>The terminal throughput snapshot, or null when the turn produced no measurement at all.</summary>
        public InvocationThroughput? ToThroughput()
        {
            var throughput = new InvocationThroughput(FirstOutputLatencyMs,
                PromptTokens,
                PromptMs,
                GenerationTokens,
                GenerationMs,
                CachedPromptTokens,
                SegmentCount);
            return throughput.IsEmpty ? null : throughput;
        }

        private static int? Add(int? total, int? value) =>
            value is null ? total : (total ?? 0) + value.Value;

        private static double? Add(double? total, double? value) =>
            value is null ? total : (total ?? 0) + value.Value;
    }

    // The single emit path both branches use: it appends to the accumulator, enforces the response/reasoning byte
    // caps, advances the sequence counter, reports the chunk to the dispatcher, and sends it over the encrypted or
    // plain hub transport. Keeping this one place guarantees the orchestration path streams byte-for-byte like the
    // single-agent path.
    private sealed class StreamTransport
    {
        private readonly InvocationExecutionContext _context;
        private readonly RuntimePackage _package;
        private readonly InvocationRunner _runner;
        private readonly bool _sendEncrypted;
        private readonly bool _sendPlain;
        private readonly IHubMessageSender _sender;

        public StreamTransport(InvocationRunner runner,
            IHubMessageSender sender,
            IWorkerEventDispatcher dispatcher,
            InvocationExecutionContext context,
            RuntimePackage package,
            bool sendEncrypted,
            bool sendPlain)
        {
            _runner = runner;
            _sender = sender;
            Dispatcher = dispatcher;
            _context = context;
            _package = package;
            _sendEncrypted = sendEncrypted;
            _sendPlain = sendPlain;
        }

        public IWorkerEventDispatcher Dispatcher { get; }

        /// <summary>
        ///     Reports a non-fatal turn notice (model substitution, tool disabled, history truncated) for this
        ///     invocation. Unlike <see cref="EmitReasoningAsync" />/<see cref="EmitTextAsync" /> this does not touch
        ///     <see cref="StreamState" /> or the byte-size caps — a notice is metadata about the turn, not streamed
        ///     model output — and reports through the SAME dispatcher every notice-emitting caller already holds, so
        ///     it needs no separate wiring.
        /// </summary>
        public Task EmitNoticeAsync(TurnNoticeKind kind, string message, string? detail = null)
        {
            return Dispatcher.ReportTurnNoticeAsync(new TurnNoticePayload
            {
                InvocationId = _package.InvocationId,
                Kind = kind,
                Message = message,
                Detail = detail
            });
        }

        // Records time-to-first-token exactly once per turn, on the first emitted reasoning OR text chunk. The terminal
        // record uses the true turn-start baseline; the existing histogram retains its model-ready baseline so cold-load
        // and generation latency remain separable. Tagged by provider (local | remote), with no model identity.
        private static void RecordFirstOutputLatency(StreamState stream)
        {
            if (stream.FirstOutputRecorded)
            {
                return;
            }

            stream.FirstOutputRecorded = true;
            stream.FirstOutputLatencyMs = Stopwatch.GetElapsedTime(stream.HarnessStartedTimestamp).TotalMilliseconds;
            if (stream.ModelReadyTimestamp is { } readyTimestamp)
            {
                NodeMetrics.ModelReadyToFirstOutputMs.Record(Stopwatch.GetElapsedTime(readyTimestamp).TotalMilliseconds,
                    new KeyValuePair<string, object?>("provider", stream.ProviderTag));
            }
        }

        public async Task EmitReasoningAsync(StreamState stream, string thinkingChunk, CancellationToken cancellationToken)
        {
            RecordFirstOutputLatency(stream);

            // Encode once: the encrypted transport needs the bytes and the size cap needs their length, so a single
            // GetBytes there feeds both. The plain and loopback paths need only the length, so they take the cheaper
            // allocation-free GetByteCount.
            var thinkingBytes = _sendEncrypted ? Encoding.UTF8.GetBytes(thinkingChunk) : null;
            stream.TotalReasoningBytes += thinkingBytes?.Length ?? Encoding.UTF8.GetByteCount(thinkingChunk);
            if (stream.TotalReasoningBytes > _runner._maxResponseSizeBytes)
            {
                throw new InvalidOperationException($"Reasoning size exceeded maximum of {_runner._maxResponseSizeBytes / (1024 * 1024)}MB");
            }

            stream.ReasoningSequence++;
            stream.ReasoningBuilder.Append(thinkingChunk);

            await Dispatcher.ReportInvocationThinkingChunkAsync(_package.InvocationId, thinkingChunk).ConfigureAwait(false);

            if (_sendEncrypted)
            {
                await _sender.SendEncryptedChunkAsync(_runner._envelopeCryptoService.EncryptChunk(_package.ConversationId,
                        _context.MessageId,
                        _context.EpochVersion,
                        _context.EpochKey.Span,
                        thinkingBytes!,
                        stream.ReasoningSequence,
                        EncryptedChunkEnvelopeV1.ReasoningKind),
                    cancellationToken).ConfigureAwait(false);
            }
            else if (_sendPlain)
            {
                await _sender.SendReasoningStreamChunkAsync(_package.InvocationId,
                    thinkingChunk,
                    isComplete: false,
                    stream.ReasoningSequence,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        public async Task EmitTextAsync(StreamState stream, string textChunk, CancellationToken cancellationToken)
        {
            RecordFirstOutputLatency(stream);

            stream.Sequence++;

            // Encode once: the encrypted transport needs the bytes and the size cap needs their length, so a single
            // GetBytes there feeds both. The plain and loopback paths need only the length, so they take the cheaper
            // allocation-free GetByteCount.
            var textBytes = _sendEncrypted ? Encoding.UTF8.GetBytes(textChunk) : null;
            stream.TotalResponseBytes += textBytes?.Length ?? Encoding.UTF8.GetByteCount(textChunk);

            if (stream.TotalResponseBytes > _runner._maxResponseSizeBytes)
            {
                throw new InvalidOperationException($"Response size exceeded maximum of {_runner._maxResponseSizeBytes / (1024 * 1024)}MB");
            }

            stream.ResponseBuilder.Append(textChunk);

            await Dispatcher.ReportInvocationStreamChunkAsync(_package.InvocationId, textChunk).ConfigureAwait(false);

            if (_sendEncrypted)
            {
                await _sender.SendEncryptedChunkAsync(_runner._envelopeCryptoService.EncryptChunk(_package.ConversationId,
                        _context.MessageId,
                        _context.EpochVersion,
                        _context.EpochKey.Span,
                        textBytes!,
                        stream.Sequence),
                    cancellationToken).ConfigureAwait(false);
            }
            else if (_sendPlain)
            {
                await _sender.SendTokenStreamChunkAsync(_package.InvocationId,
                    textChunk,
                    isComplete: false,
                    stream.Sequence,
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal sealed record UsageSnapshot(int? InputTokens, int? OutputTokens, int? ReasoningTokens, int? TotalTokens)
    {
        public static UsageSnapshot From(UsageDetails usage)
        {
            var inputTokens = ToNullableInt(usage.InputTokenCount);
            var outputTokens = ToNullableInt(usage.OutputTokenCount);
            var reasoningTokens = ToNullableInt(usage.ReasoningTokenCount);
            // Reasoning is NOT a third bucket: Microsoft.Extensions.AI documents ReasoningTokenCount as counted
            // inside OutputTokenCount, and both provider paths that reach here honour that (OpenAI reports
            // completion_tokens_details.reasoning_tokens inside completion_tokens; llama-server the same). Adding it
            // again over-counted every reasoning turn whose provider reported no total of its own. A provider-supplied
            // total always wins; with neither input nor output reported the total stays null.
            var totalTokens = ToNullableInt(usage.TotalTokenCount)
                              ?? SumIfAny(inputTokens, outputTokens);

            return new UsageSnapshot(inputTokens, outputTokens, reasoningTokens, totalTokens);
        }

        /// <summary>
        ///     Adds one provider round's usage to the running total. The first round simply becomes the total; every
        ///     round after it sums member-wise, null-preserving (a member neither side reported stays null) and
        ///     saturating at <see cref="int.MaxValue" /> so a pathological count cannot overflow the turn's total.
        /// </summary>
        public static UsageSnapshot Accumulate(UsageSnapshot? total, UsageSnapshot round)
        {
            if (total is null)
            {
                return round;
            }

            return new UsageSnapshot(Add(total.InputTokens, round.InputTokens),
                Add(total.OutputTokens, round.OutputTokens),
                Add(total.ReasoningTokens, round.ReasoningTokens),
                Add(total.TotalTokens, round.TotalTokens));
        }

        public Dictionary<string, long> ToTokenCounts()
        {
            var counts = new Dictionary<string, long>(StringComparer.Ordinal);
            AddIfPresent(counts, "inputTokens", InputTokens);
            AddIfPresent(counts, "outputTokens", OutputTokens);
            AddIfPresent(counts, "reasoningTokens", ReasoningTokens);
            AddIfPresent(counts, "totalTokens", TotalTokens);
            return counts;
        }

        private static void AddIfPresent(Dictionary<string, long> counts, string key, int? value)
        {
            if (value is not null)
            {
                counts[key] = value.Value;
            }
        }

        private static int? SumIfAny(params int?[] values)
        {
            return values.Any(static value => value is not null)
                ? values.Sum(static value => value ?? 0)
                : null;
        }

        // Saturating member-wise add, mirroring ToNullableInt's clamp: two in-range rounds can still sum past
        // int.MaxValue, and a token total must not wrap negative because a provider reported an absurd count.
        private static int? Add(int? total, int? value)
        {
            if (value is null)
            {
                return total;
            }

            var sum = (long)(total ?? 0) + value.Value;
            return sum > int.MaxValue ? int.MaxValue : (int)sum;
        }

        private static int? ToNullableInt(long? value)
        {
            if (value is null)
            {
                return null;
            }

            // Token counts are non-negative and effectively always in int range, but a provider reporting a count past
            // int.MaxValue must not fault the whole stream mid-flight: saturate at int.MaxValue instead of throwing.
            return value.Value > int.MaxValue ? int.MaxValue : (int)value.Value;
        }
    }
}
