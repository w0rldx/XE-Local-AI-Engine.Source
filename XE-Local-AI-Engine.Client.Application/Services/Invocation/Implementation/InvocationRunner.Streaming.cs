namespace XE_Local_AI_Engine.Client.Services.Invocation.Implementation;

using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Encrypted;
using XE_Local_AI_Engine.Client.Models.Events;
using XE_Local_AI_Engine.Client.Services.Connection;
using XE_Local_AI_Engine.Client.Services.Events;

public sealed partial class InvocationRunner
{
    // The mutable streaming accumulator shared by the single-agent and orchestration paths: the response/reasoning
    // builders, the byte totals (against _maxResponseSizeBytes), the monotonic sequence counters the transport sends,
    // and the terminal usage snapshot. Carried by reference into the branch methods so the post-stream completion
    // block in RunAsync reads the final state.
    private sealed class StreamState
    {
        // Wall-clock generation timer for the whole turn (prompt-eval through final token), started at state
        // construction so it covers both the single-agent and orchestration branches. Read once in the completion
        // block to stamp the persisted tokens-per-second duration.
        public Stopwatch GenerationStopwatch { get; } = Stopwatch.StartNew();

        public StringBuilder ResponseBuilder { get; } = new();

        public StringBuilder ReasoningBuilder { get; } = new();

        public UsageSnapshot? UsageSnapshot { get; set; }

        public long Sequence { get; set; }

        public long ReasoningSequence { get; set; }

        public int TotalResponseBytes { get; set; }

        public int TotalReasoningBytes { get; set; }
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

        public async Task EmitReasoningAsync(StreamState stream, string thinkingChunk, CancellationToken cancellationToken)
        {
            stream.TotalReasoningBytes += Encoding.UTF8.GetByteCount(thinkingChunk);
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
                        Encoding.UTF8.GetBytes(thinkingChunk),
                        stream.ReasoningSequence,
                        EncryptedChunkEnvelopeV1.ReasoningKind),
                    cancellationToken).ConfigureAwait(false);
            }
            else if (_sendPlain)
            {
                await _sender.SendReasoningStreamChunkAsync(_package.InvocationId,
                    thinkingChunk,
                    false,
                    stream.ReasoningSequence,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        public async Task EmitTextAsync(StreamState stream, string textChunk, CancellationToken cancellationToken)
        {
            stream.Sequence++;
            stream.TotalResponseBytes += Encoding.UTF8.GetByteCount(textChunk);

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
                        Encoding.UTF8.GetBytes(textChunk),
                        stream.Sequence),
                    cancellationToken).ConfigureAwait(false);
            }
            else if (_sendPlain)
            {
                await _sender.SendTokenStreamChunkAsync(_package.InvocationId,
                    textChunk,
                    false,
                    stream.Sequence,
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private sealed record UsageSnapshot(int? InputTokens, int? OutputTokens, int? ReasoningTokens, int? TotalTokens)
    {
        public static UsageSnapshot From(UsageDetails usage)
        {
            var inputTokens = ToNullableInt(usage.InputTokenCount);
            var outputTokens = ToNullableInt(usage.OutputTokenCount);
            var reasoningTokens = ToNullableInt(usage.ReasoningTokenCount);
            var totalTokens = ToNullableInt(usage.TotalTokenCount)
                              ?? SumIfAny(inputTokens, outputTokens, reasoningTokens);

            return new UsageSnapshot(inputTokens, outputTokens, reasoningTokens, totalTokens);
        }

        public Dictionary<string, long> ToTokenCounts()
        {
            var counts = new Dictionary<string, long>();
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

        private static int? ToNullableInt(long? value)
        {
            return value is null ? null : checked((int)value.Value);
        }
    }

    private sealed record PendingToolCall(
        Guid InvocationId,
        DateTimeOffset CreatedAt,
        TaskCompletionSource<bool> ApprovalCompletion,
        TaskCompletionSource<ToolCallResultEvent> ResultCompletion);
}
