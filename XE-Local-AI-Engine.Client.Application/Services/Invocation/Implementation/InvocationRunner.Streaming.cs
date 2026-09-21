namespace XE_Local_AI_Engine.Client.Services.Invocation.Implementation;

using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Client.Common.Telemetry;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

public sealed partial class InvocationRunner
{
    // The mutable streaming accumulator both paths share — builders, byte totals, sequence counters, usage snapshot —
    // carried by reference so RunAsync's completion block reads it. Internal so LocalRuntimeWarmer writes readiness in.
    internal sealed class StreamState
    {
        // Wall-clock timer for the whole turn, started at construction so it covers both branches. Read once in the
        // completion block to stamp the persisted tokens-per-second duration.
        public Stopwatch GenerationStopwatch { get; } = Stopwatch.StartNew();

        // TTFT inputs: HarnessStartedTimestamp is the harness measure on the efficiency record, ModelReadyTimestamp
        // the inference-only one, ProviderTag the bounded dimension, FirstOutputRecorded the one-shot gate for both.
        public long HarnessStartedTimestamp { get; init; }

        public long? ModelReadyTimestamp { get; set; }

        public double? ModelReadinessDurationMs { get; private set; }

        /// <summary>
        ///     Adds one local warm's duration to the turn's readiness total.
        /// </summary>
        /// <remarks>
        ///     SUMMED, not assigned: a turn can warm twice — a dispatched fast model failing before first output is
        ///     followed by a warm of the original for the fallback — and an assignment charges only the second while
        ///     the whole-turn clock still contains both.
        /// </remarks>
        public void AddModelReadiness(double durationMs)
        {
            ModelReadinessDurationMs = (ModelReadinessDurationMs ?? 0d) + durationMs;
        }

        public double? FirstOutputLatencyMs { get; set; }

        public string ProviderTag { get; set; } = "remote";

        public bool FirstOutputRecorded { get; set; }

        public StringBuilder ResponseBuilder { get; } = new();

        public StringBuilder ReasoningBuilder { get; } = new();

        // The turn's usage, ACCUMULATED across rounds: the tool loop runs inside ONE RunStreamingAsync, so last-wins
        // would record only the final round. This is what the turn COST; the private setter forces AddUsage.
        public UsageSnapshot? UsageSnapshot { get; private set; }

        // The LAST round's usage alone. A round's prompt is the WHOLE conversation, so its input count is what the
        // context HELD — the occupancy the chat meter shows. Cost sums; occupancy does not.
        public UsageSnapshot? LastRoundUsage { get; private set; }

        /// <summary>Records one provider round's reported usage as the last round and folds it into the turn totals.</summary>
        public void AddUsage(UsageDetails usage)
        {
            LastRoundUsage = UsageSnapshot.From(usage);
            UsageSnapshot = UsageSnapshot.Accumulate(UsageSnapshot, LastRoundUsage);
        }

        // Why generation stopped, from the LAST update carrying a reason: a tool-calling turn ends on "stop" after
        // "tool_calls". Verbatim, because llama-server's "length" covers both n_predict and a filled window.
        public string? FinishReason { get; set; }

        public long Sequence { get; set; }

        public long ReasoningSequence { get; set; }

        public int TotalResponseBytes { get; set; }

        public int TotalReasoningBytes { get; set; }

        // llama-server's pp/tg timings SUMMED across every provider request the turn made. TTFT is deliberately NOT
        // summed — FirstOutputLatencyMs is one-shot on the first chunk. Null throughout for a provider with no timings.
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
            var throughput = new InvocationThroughput
            {
                TimeToFirstTokenMs = FirstOutputLatencyMs,
                PromptTokens = PromptTokens,
                PromptMs = PromptMs,
                GenerationTokens = GenerationTokens,
                GenerationMs = GenerationMs,
                CachedPromptTokens = CachedPromptTokens,
                SegmentCount = SegmentCount
            };
            return throughput.IsEmpty ? null : throughput;
        }

        private static int? Add(int? total, int? value) =>
            value is null ? total : (total ?? 0) + value.Value;

        private static double? Add(double? total, double? value) =>
            value is null ? total : (total ?? 0) + value.Value;
    }

    // The single emit path both branches use: append, enforce the byte caps, advance the sequence, report. One place,
    // so the orchestration path streams byte-for-byte like the single-agent path.
    private sealed class StreamTransport
    {
        private readonly RuntimePackage _package;
        private readonly InvocationRunner _runner;

        public StreamTransport(InvocationRunner runner,
            IWorkerEventDispatcher dispatcher,
            RuntimePackage package)
        {
            _runner = runner;
            Dispatcher = dispatcher;
            _package = package;
        }

        public IWorkerEventDispatcher Dispatcher { get; }

        /// <summary>
        ///     Reports a non-fatal turn notice (model substitution, tool disabled, history truncated) for this
        ///     invocation.
        /// </summary>
        /// <remarks>
        ///     Unlike <see cref="EmitReasoningAsync" />/<see cref="EmitTextAsync" /> it touches neither
        ///     <see cref="StreamState" /> nor the byte caps, a notice being metadata rather than model output, and it
        ///     reports through the SAME dispatcher every notice-emitting caller holds, so it needs no wiring.
        /// </remarks>
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

        // Records TTFT once per turn, on the first reasoning OR text chunk. The record uses the turn-start baseline
        // and the histogram the model-ready one, so cold-load and generation stay separable. No model identity.
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

        public async Task EmitReasoningAsync(StreamState stream, string thinkingChunk)
        {
            RecordFirstOutputLatency(stream);

            // The size cap needs only the encoded length, so it takes the allocation-free GetByteCount.
            stream.TotalReasoningBytes += Encoding.UTF8.GetByteCount(thinkingChunk);
            if (stream.TotalReasoningBytes > _runner._maxResponseSizeBytes)
            {
                throw new InvalidOperationException($"Reasoning size exceeded maximum of {_runner._maxResponseSizeBytes / (1024 * 1024)}MB");
            }

            stream.ReasoningSequence++;
            stream.ReasoningBuilder.Append(thinkingChunk);

            await Dispatcher.ReportInvocationThinkingChunkAsync(_package.InvocationId, thinkingChunk);
        }

        public async Task EmitTextAsync(StreamState stream, string textChunk)
        {
            RecordFirstOutputLatency(stream);

            stream.Sequence++;

            // The size cap needs only the encoded length, so it takes the allocation-free GetByteCount.
            stream.TotalResponseBytes += Encoding.UTF8.GetByteCount(textChunk);

            if (stream.TotalResponseBytes > _runner._maxResponseSizeBytes)
            {
                throw new InvalidOperationException($"Response size exceeded maximum of {_runner._maxResponseSizeBytes / (1024 * 1024)}MB");
            }

            stream.ResponseBuilder.Append(textChunk);

            await Dispatcher.ReportInvocationStreamChunkAsync(_package.InvocationId, textChunk);
        }
    }

    internal sealed class UsageSnapshot
    {
        public required int? InputTokens { get; init; }

        public required int? OutputTokens { get; init; }

        public required int? ReasoningTokens { get; init; }

        public required int? TotalTokens { get; init; }

        public static UsageSnapshot From(UsageDetails usage)
        {
            var inputTokens = ToNullableInt(usage.InputTokenCount);
            var outputTokens = ToNullableInt(usage.OutputTokenCount);
            var reasoningTokens = ToNullableInt(usage.ReasoningTokenCount);
            // Reasoning is NOT a third bucket: ReasoningTokenCount is counted INSIDE OutputTokenCount and both
            // provider paths honour that. A provider-supplied total wins; with neither count reported it stays null.
            var totalTokens = ToNullableInt(usage.TotalTokenCount)
                              ?? SumIfAny(inputTokens, outputTokens);

            return new UsageSnapshot { InputTokens = inputTokens, OutputTokens = outputTokens, ReasoningTokens = reasoningTokens, TotalTokens = totalTokens };
        }

        /// <summary>
        ///     Adds one provider round's usage to the running total; the first round simply becomes the total.
        /// </summary>
        /// <remarks>
        ///     Every round after it sums member-wise, null-preserving (a member neither side reported stays null) and
        ///     saturating at <see cref="int.MaxValue" />, so a pathological count cannot overflow the turn's total.
        /// </remarks>
        public static UsageSnapshot Accumulate(UsageSnapshot? total, UsageSnapshot round)
        {
            if (total is null)
            {
                return round;
            }

            return new UsageSnapshot
            {
                InputTokens = Add(total.InputTokens, round.InputTokens),
                OutputTokens = Add(total.OutputTokens, round.OutputTokens),
                ReasoningTokens = Add(total.ReasoningTokens, round.ReasoningTokens),
                TotalTokens = Add(total.TotalTokens, round.TotalTokens)
            };
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

        // Folded onto the saturating Add so a DERIVED total clamps like an accumulated one; a checked sum of two
        // in-range counts past int.MaxValue faults the stream. Null-preserving when neither side reported.
        private static int? SumIfAny(int? left, int? right) =>
            left is null && right is null ? null : Add(left, right);

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
